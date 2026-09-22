using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VMiner.Models;

namespace VMiner.Services;

internal sealed class SupabaseVocabularyBackend(
    SupabaseAuthService auth,
    HttpClient? httpClient = null) : IVocabularyDatabaseBackend, IDisposable
{
    private const int ImportBatchSize = 200;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();
    private readonly bool _ownsHttpClient = httpClient is null;

    public bool IsConnected => auth.IsAuthenticated;

    public async Task<bool> ImportLocalDatabaseIfEmptyAsync(
        string? localPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            return false;

        var remote = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (remote.Entries.Count > 0)
            return false;

        await using var stream = File.OpenRead(localPath);
        var local = await JsonSerializer.DeserializeAsync<VocabularyDatabase>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (local is null || local.Entries.Count == 0)
            return false;

        foreach (var entry in local.Entries)
        {
            if (entry.Examples.Count == 0)
            {
                await AddOrUpdateAsync(entry.Word, entry.Reading, entry.Definition, "", "",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (var example in entry.Examples)
            {
                await AddOrUpdateAsync(
                    entry.Word, entry.Reading, entry.Definition,
                    example.Japanese, example.English, cancellationToken).ConfigureAwait(false);
            }
        }
        return true;
    }

    public async Task<VocabularyDatabase> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        _ = auth.UserId ?? throw new InvalidOperationException("Sign in to VMiner first.");
        const int pageSize = 1000;
        const string basePath = "/rest/v1/vocabulary_entries" +
                                "?select=id,word,reading,definition,sentence_examples(id,japanese,english)" +
                                "&order=word.asc,id.asc";
        var rows = new List<VocabularyEntryRow>();
        var offset = 0;
        while (true)
        {
            var path = basePath + $"&limit={pageSize}&offset={offset}";
            using var response = await SendAuthorizedAsync(
                HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            EnsureSuccess(response, json);
            var page = JsonSerializer.Deserialize<List<VocabularyEntryRow>>(json, JsonOptions)
                       ?? [];
            if (page.Count == 0)
                break;
            rows.AddRange(page);
            offset += page.Count;
        }

        return new VocabularyDatabase
        {
            Entries = rows.Select(row => new VocabularyEntry
            {
                Id = row.Id,
                Word = row.Word,
                Reading = row.Reading,
                Definition = row.Definition,
                Examples = row.Examples.Select(example => new SentencePair
                {
                    Id = example.Id,
                    Japanese = example.Japanese,
                    English = example.English,
                }).ToList(),
            }).ToList(),
        };
    }

    public async Task<Guid> AddOrUpdateAsync(
        string word,
        string reading,
        string definition,
        string japaneseSentence,
        string englishSentence,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            p_word = word,
            p_reading = reading,
            p_definition = definition,
            p_japanese = japaneseSentence,
            p_english = englishSentence,
        };
        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/rest/v1/rpc/add_or_update_vocabulary",
            body,
            cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, json);
        return JsonSerializer.Deserialize<Guid>(json, JsonOptions);
    }

    public async Task ReplaceAsync(
        Guid entryId,
        VocabularyEntry entry,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            p_entry_id = entryId,
            p_word = entry.Word,
            p_reading = entry.Reading,
            p_definition = entry.Definition,
            p_examples = entry.Examples.Select(example => new
            {
                japanese = example.Japanese,
                english = example.English,
            }).ToArray(),
        };
        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/rest/v1/rpc/replace_vocabulary_entry",
            body,
            cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, json);
    }

    public async Task DeleteAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        var path = "/rest/v1/vocabulary_entries?id=eq." +
                   Uri.EscapeDataString(entryId.ToString());
        using var response = await SendAuthorizedAsync(
            HttpMethod.Delete, path, null, cancellationToken, "return=minimal")
            .ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, json);
    }

    public async Task<VocabularyImportResult> ImportWaniKaniAsync(
        IReadOnlyList<WaniKaniVocabularyItem> entries,
        IProgress<WaniKaniImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new VocabularyImportResult(0, 0, 0);
        var completed = 0;
        foreach (var batch in entries.Chunk(ImportBatchSize))
        {
            var body = new
            {
                p_entries = batch.Select(entry => new
                {
                    subject_id = entry.SubjectId,
                    updated_at = entry.UpdatedAt,
                    word = entry.Word,
                    reading = entry.Reading,
                    definition = entry.Definition,
                    examples = entry.Examples.Select(example => new
                    {
                        japanese = example.Japanese,
                        english = example.English,
                    }).ToArray(),
                }).ToArray(),
            };
            using var response = await SendAuthorizedAsync(
                HttpMethod.Post,
                "/rest/v1/rpc/import_wanikani_vocabulary",
                body,
                cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode &&
                IsMissingRpc(json, "import_wanikani_vocabulary"))
            {
                return await ImportWaniKaniCompatibilityAsync(
                    entries, progress, cancellationToken).ConfigureAwait(false);
            }
            EnsureSuccess(response, json);
            var batchResult = JsonSerializer.Deserialize<WaniKaniBatchResult>(json, JsonOptions)
                              ?? throw new InvalidOperationException(
                                  "Supabase returned an empty WaniKani import result.");
            result += new VocabularyImportResult(
                batchResult.NewEntries,
                batchResult.ExistingEntries,
                batchResult.NewExamples);
            completed += batch.Length;
            progress?.Report(new WaniKaniImportProgress(
                $"Saving WaniKani vocabulary… {completed:N0}/{entries.Count:N0}",
                completed, entries.Count));
        }
        return result;
    }

    private async Task<VocabularyImportResult> ImportWaniKaniCompatibilityAsync(
        IReadOnlyList<WaniKaniVocabularyItem> entries,
        IProgress<WaniKaniImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var result = new VocabularyImportResult(0, 0, 0);
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var imported = entries[index];
            var existing = current.Entries.FirstOrDefault(item =>
                string.Equals(item.Word, imported.Word, StringComparison.Ordinal) &&
                string.Equals(item.Reading, imported.Reading, StringComparison.Ordinal));
            var definition = existing?.Definition ?? imported.Definition;
            if (existing is null)
            {
                existing = new VocabularyEntry
                {
                    Word = imported.Word,
                    Reading = imported.Reading,
                    Definition = definition,
                };
                current.Entries.Add(existing);
                result += new VocabularyImportResult(1, 0, 0);
            }
            else
            {
                result += new VocabularyImportResult(0, 1, 0);
            }

            var missingExamples = imported.Examples.Where(example =>
                    !existing.ContainsExample(example.Japanese, example.English))
                .ToArray();
            if (missingExamples.Length == 0 && existing.Id == Guid.Empty)
            {
                await AddOrUpdateAsync(
                    imported.Word, imported.Reading, definition, "", "", cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var example in missingExamples)
            {
                await AddOrUpdateAsync(
                    imported.Word,
                    imported.Reading,
                    definition,
                    example.Japanese,
                    example.English,
                    cancellationToken).ConfigureAwait(false);
                existing.Examples.Add(new SentencePair
                {
                    Japanese = example.Japanese,
                    English = example.English,
                });
            }
            result += new VocabularyImportResult(0, 0, missingExamples.Length);
            progress?.Report(new WaniKaniImportProgress(
                $"Saving WaniKani vocabulary… {index + 1:N0}/{entries.Count:N0}",
                index + 1, entries.Count));
        }
        return result;
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken,
        string? prefer = null)
    {
        var token = await auth.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendOnceAsync(
            method, path, body, token, cancellationToken, prefer).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        token = await auth.ForceRefreshAsync(cancellationToken).ConfigureAwait(false);
        return await SendOnceAsync(
            method, path, body, token, cancellationToken, prefer).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method,
        string path,
        object? body,
        string token,
        CancellationToken cancellationToken,
        string? prefer)
    {
        using var request = auth.CreateAuthorizedRequest(method, path, token);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        if (!string.IsNullOrWhiteSpace(prefer))
            request.Headers.TryAddWithoutValidation("Prefer", prefer);
        return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureSuccess(HttpResponseMessage response, string json)
    {
        if (!response.IsSuccessStatusCode)
        {
            if (json.Contains("Could not find the function public.",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The Supabase database schema is outdated. Run the current " +
                    "supabase-setup.sql in the Supabase SQL Editor, then try again.");
            }
            throw new InvalidOperationException(
                SupabaseAuthService.ReadError(json, response.ReasonPhrase));
        }
    }

    private static bool IsMissingRpc(string json, string functionName) =>
        json.Contains("Could not find the function", StringComparison.OrdinalIgnoreCase) &&
        json.Contains(functionName, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private sealed class VocabularyEntryRow
    {
        public Guid Id { get; init; }
        public string Word { get; init; } = "";
        public string Reading { get; init; } = "";
        public string Definition { get; init; } = "";

        [JsonPropertyName("sentence_examples")]
        public List<SentenceExampleRow> Examples { get; init; } = [];
    }

    private sealed class SentenceExampleRow
    {
        public Guid Id { get; init; }
        public string Japanese { get; init; } = "";
        public string English { get; init; } = "";
    }

    private sealed class WaniKaniBatchResult
    {
        [JsonPropertyName("new_entries")]
        public int NewEntries { get; init; }

        [JsonPropertyName("existing_entries")]
        public int ExistingEntries { get; init; }

        [JsonPropertyName("new_examples")]
        public int NewExamples { get; init; }
    }
}
