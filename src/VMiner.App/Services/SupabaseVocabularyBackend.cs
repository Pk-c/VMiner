using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using VMiner.Models;

namespace VMiner.Services;

internal sealed class SupabaseVocabularyBackend(
    SupabaseAuthService auth,
    HttpClient? httpClient = null) : IVocabularyDatabaseBackend, IDisposable
{
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

        await SaveAsync(local, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<VocabularyDatabase> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var userId = auth.UserId
                     ?? throw new InvalidOperationException("Sign in to VMiner first.");
        var path = "/rest/v1/vocabulary_databases?select=data&user_id=eq." +
                   Uri.EscapeDataString(userId) + "&limit=1";
        using var response = await SendAuthorizedAsync(
            HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, json);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() == 0)
            return new VocabularyDatabase();

        var row = document.RootElement[0];
        if (!row.TryGetProperty("data", out var data))
            return new VocabularyDatabase();
        return data.Deserialize<VocabularyDatabase>(JsonOptions)
               ?? new VocabularyDatabase();
    }

    public async Task SaveAsync(
        VocabularyDatabase database,
        CancellationToken cancellationToken = default)
    {
        var userId = auth.UserId
                     ?? throw new InvalidOperationException("Sign in to VMiner first.");
        var body = new
        {
            user_id = userId,
            data = database,
            updated_at = DateTimeOffset.UtcNow,
        };
        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/rest/v1/vocabulary_databases?on_conflict=user_id",
            body,
            cancellationToken,
            "resolution=merge-duplicates,return=minimal").ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, json);
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
            throw new InvalidOperationException(
                SupabaseAuthService.ReadError(json, response.ReasonPhrase));
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
