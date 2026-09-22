using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using VMiner.Models;

namespace VMiner.Services;

internal sealed class WaniKaniImportService(HttpClient? httpClient = null) : IDisposable
{
    private const string Revision = "20170710";
    private const int SubjectBatchSize = 250;
    private static readonly Uri ApiRoot = new("https://api.wanikani.com/v2/");
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();
    private readonly bool _ownsHttpClient = httpClient is null;

    public async Task<WaniKaniFetchResult> FetchStudiedVocabularyAsync(
        string apiToken,
        IProgress<WaniKaniImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        apiToken = apiToken.Trim();
        if (apiToken.Length == 0)
            throw new InvalidOperationException("Enter a WaniKani API token.");

        progress?.Report(new WaniKaniImportProgress("Checking WaniKani account…", 0));
        using var userDocument = await GetJsonAsync(
            new Uri(ApiRoot, "user"), apiToken, cancellationToken).ConfigureAwait(false);
        if (!TryGetObject(userDocument.RootElement, "data", out var userData))
            throw UnexpectedResponse("user information");
        var username = ReadString(userData, "username") ?? "WaniKani user";
        if (!TryGetObject(userData, "subscription", out var subscription) ||
            !TryGetInt32(subscription, "max_level_granted", out var maxLevel))
        {
            throw UnexpectedResponse("subscription information");
        }

        var subjectIds = new HashSet<long>();
        Uri? nextPage = new(ApiRoot,
            "assignments?subject_types=vocabulary,kana_vocabulary&started=true&hidden=false");
        var assignmentCount = 0;
        while (nextPage is not null)
        {
            using var page = await GetJsonAsync(nextPage, apiToken, cancellationToken)
                .ConfigureAwait(false);
            var root = page.RootElement;
            if (!TryGetArray(root, "data", out var assignments))
                throw UnexpectedResponse("assignments");
            foreach (var assignment in assignments.EnumerateArray())
            {
                if (TryGetObject(assignment, "data", out var data) &&
                    TryGetInt64(data, "subject_id", out var subjectId))
                {
                    subjectIds.Add(subjectId);
                }
                assignmentCount++;
            }
            progress?.Report(new WaniKaniImportProgress(
                $"Reading studied vocabulary… {assignmentCount:N0}", assignmentCount));
            nextPage = ReadNextPage(root);
        }

        var entries = new List<WaniKaniVocabularyItem>(subjectIds.Count);
        var completed = 0;
        foreach (var batch in subjectIds.Chunk(SubjectBatchSize))
        {
            var ids = string.Join(',', batch);
            using var subjects = await GetJsonAsync(
                new Uri(ApiRoot, "subjects?ids=" + ids), apiToken, cancellationToken)
                .ConfigureAwait(false);
            if (!TryGetArray(subjects.RootElement, "data", out var subjectItems))
                throw UnexpectedResponse("subjects");
            foreach (var subject in subjectItems.EnumerateArray())
            {
                var mapped = MapSubject(subject, maxLevel);
                if (mapped is not null)
                    entries.Add(mapped);
            }
            completed += batch.Length;
            progress?.Report(new WaniKaniImportProgress(
                $"Downloading WaniKani vocabulary… {completed:N0}/{subjectIds.Count:N0}",
                completed, subjectIds.Count));
        }

        return new WaniKaniFetchResult(
            username,
            entries.OrderBy(entry => entry.Word, StringComparer.Ordinal).ToArray());
    }

    private async Task<JsonDocument> GetJsonAsync(
        Uri uri,
        string apiToken,
        CancellationToken cancellationToken)
    {
        EnsureWaniKaniUri(uri);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            request.Headers.TryAddWithoutValidation("Wanikani-Revision", Revision);
            using var response = await _httpClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
            {
                await Task.Delay(ReadRetryDelay(response), cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException(
                    "WaniKani rejected this token. Create a valid read-only API token and try again.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(ReadApiError(json, response.ReasonPhrase));
            return JsonDocument.Parse(json);
        }
    }

    private static WaniKaniVocabularyItem? MapSubject(JsonElement subject, int maxLevel)
    {
        var objectType = ReadString(subject, "object");
        if (objectType is not ("vocabulary" or "kana_vocabulary"))
            return null;

        if (!TryGetObject(subject, "data", out var data) ||
            !TryGetInt64(subject, "id", out var subjectId))
        {
            return null;
        }
        if (TryGetInt32(data, "level", out var level) && level > maxLevel)
            return null;
        if (data.TryGetProperty("hidden_at", out var hidden) && hidden.ValueKind != JsonValueKind.Null)
            return null;
        var word = ReadString(data, "characters")?.Trim() ?? "";
        var reading = objectType == "kana_vocabulary"
            ? word
            : TryGetArray(data, "readings", out var readings)
                ? ReadPrimaryString(readings, "reading")
                : "";
        var definition = TryGetArray(data, "meanings", out var meanings)
            ? ReadPrimaryString(meanings, "meaning")
            : "";
        if (word.Length == 0 || reading.Length == 0 || definition.Length == 0)
            return null;

        var examples = new List<SentencePair>();
        if (TryGetArray(data, "context_sentences", out var contextSentences))
        {
            foreach (var context in contextSentences.EnumerateArray())
            {
                var japanese = ReadString(context, "ja")?.Trim() ?? "";
                if (japanese.Length == 0)
                    continue;
                examples.Add(new SentencePair
                {
                    Japanese = japanese,
                    English = ReadString(context, "en")?.Trim() ?? "",
                });
            }
        }

        var updatedAt = subject.TryGetProperty("data_updated_at", out var updated) &&
                        updated.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(updated.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
        return new WaniKaniVocabularyItem(
            subjectId,
            updatedAt,
            word,
            reading,
            definition,
            examples);
    }

    private static string ReadPrimaryString(JsonElement values, string propertyName)
    {
        string? fallback = null;
        foreach (var value in values.EnumerateArray())
        {
            var text = ReadString(value, propertyName)?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;
            fallback ??= text;
            if (value.TryGetProperty("primary", out var primary) &&
                primary.ValueKind is JsonValueKind.True)
            {
                return text;
            }
        }
        return fallback ?? "";
    }

    private static bool TryGetObject(
        JsonElement parent,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(propertyName, out value) &&
               value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetArray(
        JsonElement parent,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(propertyName, out value) &&
               value.ValueKind == JsonValueKind.Array;
    }

    private static string? ReadString(JsonElement parent, string propertyName) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetInt32(
        JsonElement parent,
        string propertyName,
        out int value)
    {
        value = 0;
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(propertyName, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out value);
    }

    private static bool TryGetInt64(
        JsonElement parent,
        string propertyName,
        out long value)
    {
        value = 0;
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(propertyName, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt64(out value);
    }

    private static InvalidOperationException UnexpectedResponse(string section) => new(
        $"WaniKani returned incomplete {section}. Please try again later or update VMiner.");

    private static Uri? ReadNextPage(JsonElement root)
    {
        if (!TryGetObject(root, "pages", out var pages) ||
            !pages.TryGetProperty("next_url", out var next) ||
            next.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (next.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(next.GetString(), UriKind.Absolute, out var uri))
        {
            throw UnexpectedResponse("pagination information");
        }
        EnsureWaniKaniUri(uri);
        return uri;
    }

    private static void EnsureWaniKaniUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "api.wanikani.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("WaniKani returned an invalid API address.");
    }

    private static TimeSpan ReadRetryDelay(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("RateLimit-Reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), out var reset))
        {
            var wait = DateTimeOffset.FromUnixTimeSeconds(reset) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero && wait <= TimeSpan.FromMinutes(2))
                return wait + TimeSpan.FromSeconds(1);
        }
        return TimeSpan.FromSeconds(10);
    }

    private static string ReadApiError(string json, string? fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out var error))
                return "WaniKani request failed: " + error.GetString();
        }
        catch (JsonException)
        {
        }
        return "WaniKani request failed: " + (fallback ?? "unknown error");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
