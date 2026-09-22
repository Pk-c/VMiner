using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VMiner.Services;

internal sealed class SupabaseOptions
{
    public string Url { get; init; } = "";
    public string PublishableKey { get; init; } = "";

    public bool IsValid => Uri.TryCreate(Url, UriKind.Absolute, out var uri) &&
                           uri.Scheme == Uri.UriSchemeHttps &&
                           IsSafePublicKey(PublishableKey);

    private static bool IsSafePublicKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) ||
            key.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("sb_secret_", StringComparison.Ordinal))
            return false;
        if (key.StartsWith("sb_publishable_", StringComparison.Ordinal))
            return true;

        // Legacy Supabase anon keys are JWTs. Accept only an explicit anon role,
        // never a service_role token that would bypass Row Level Security.
        var segments = key.Split('.');
        if (segments.Length != 3)
            return false;
        try
        {
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("role", out var role) &&
                   string.Equals(role.GetString(), "anon", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}

internal sealed record SupabaseSession(
    string AccessToken,
    string RefreshToken,
    string UserId,
    string Email,
    DateTimeOffset ExpiresAt);

internal sealed record SignUpResult(bool SignedIn, string Message);

internal sealed class SupabaseAuthService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ISupabaseSessionStore _sessionStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SupabaseSession? _session;

    public SupabaseAuthService()
        : this(LoadOptions(), new HttpClient(), new EncryptedSessionStore(), true)
    {
    }

    internal SupabaseAuthService(
        SupabaseOptions options,
        HttpClient httpClient,
        ISupabaseSessionStore sessionStore,
        bool ownsHttpClient = false)
    {
        Options = options;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _sessionStore = sessionStore;
    }

    public static string ConfigurationPath { get; } = Path.Combine(
        AppContext.BaseDirectory, "supabase-config.json");

    public SupabaseOptions Options { get; }
    public bool IsConfigured => Options.IsValid;
    public bool HasStoredSession => _sessionStore.HasSession;
    public bool IsAuthenticated => _session is not null;
    public string? Email => _session?.Email;
    public string? UserId => _session?.UserId;

    public async Task<bool> TryRestoreSessionAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await _sessionStore.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (stored is null || string.IsNullOrWhiteSpace(stored.RefreshToken))
            {
                await _sessionStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            try
            {
                _session = await RefreshUnsafeAsync(stored.RefreshToken, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch
            {
                _session = null;
                await _sessionStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignInAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await SendAuthAsync(
                "/auth/v1/token?grant_type=password",
                new { email = email.Trim(), password },
                cancellationToken).ConfigureAwait(false);
            _session = CreateSession(response);
            await _sessionStore.SaveAsync(_session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SignUpResult> SignUpAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await SendAuthAsync(
                "/auth/v1/signup",
                new { email = email.Trim(), password },
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.AccessToken))
            {
                return new SignUpResult(false,
                    "Account created. Check your email to confirm it, then sign in.");
            }

            _session = CreateSession(response);
            await _sessionStore.SaveAsync(_session, cancellationToken).ConfigureAwait(false);
            return new SignUpResult(true, "Account created and signed in.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> GetAccessTokenAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is null)
                throw new InvalidOperationException("Sign in to your VMiner account first.");
            if (_session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
                _session = await RefreshUnsafeAsync(_session.RefreshToken, cancellationToken)
                    .ConfigureAwait(false);
            return _session.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ForceRefreshAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is null)
                throw new InvalidOperationException("Sign in to your VMiner account first.");
            _session = await RefreshUnsafeAsync(_session.RefreshToken, cancellationToken)
                .ConfigureAwait(false);
            return _session.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null && IsConfigured)
            {
                try
                {
                    using var request = CreateRequest(HttpMethod.Post, "/auth/v1/logout");
                    request.Headers.Authorization = new(
                        "Bearer", _session.AccessToken);
                    using var response = await _httpClient.SendAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Local sign-out must still succeed when the network is unavailable.
                }
            }

            _session = null;
            await _sessionStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        string relativePath,
        string accessToken)
    {
        var request = CreateRequest(method, relativePath);
        request.Headers.Authorization = new("Bearer", accessToken);
        return request;
    }

    private async Task<SupabaseSession> RefreshUnsafeAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var response = await SendAuthAsync(
            "/auth/v1/token?grant_type=refresh_token",
            new { refresh_token = refreshToken },
            cancellationToken).ConfigureAwait(false);
        var session = CreateSession(response);
        await _sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task<AuthResponse> SendAuthAsync(
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(body);
        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadError(json, response.ReasonPhrase));
        return JsonSerializer.Deserialize<AuthResponse>(json, JsonOptions)
               ?? throw new InvalidOperationException("Supabase returned an empty response.");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        EnsureConfigured();
        var request = new HttpRequestMessage(
            method, Options.Url.TrimEnd('/') + relativePath);
        request.Headers.Add("apikey", Options.PublishableKey);
        return request;
    }

    internal static string ReadError(string json, string? fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            foreach (var property in new[] { "message", "msg", "error_description", "error" })
            {
                if (root.TryGetProperty(property, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(fallback)
            ? "Supabase request failed."
            : $"Supabase request failed: {fallback}";
    }

    private static SupabaseSession CreateSession(AuthResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.AccessToken) ||
            string.IsNullOrWhiteSpace(response.RefreshToken) ||
            string.IsNullOrWhiteSpace(response.User?.Id))
            throw new InvalidOperationException("Supabase did not return a valid user session.");

        var expiresAt = response.ExpiresAt is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(response.ExpiresAt.Value)
            : DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, response.ExpiresIn ?? 3600));
        return new SupabaseSession(
            response.AccessToken,
            response.RefreshToken,
            response.User.Id,
            response.User.Email ?? "",
            expiresAt);
    }

    private static SupabaseOptions LoadOptions()
    {
        try
        {
            if (!File.Exists(ConfigurationPath))
                return new SupabaseOptions();
            return JsonSerializer.Deserialize<SupabaseOptions>(
                       File.ReadAllText(ConfigurationPath), JsonOptions)
                   ?? new SupabaseOptions();
        }
        catch
        {
            return new SupabaseOptions();
        }
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Supabase is not configured. Place supabase-config.json next to VMiner.exe.");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
        _gate.Dispose();
    }

    private sealed class AuthResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expires_in")]
        public long? ExpiresIn { get; init; }

        [JsonPropertyName("expires_at")]
        public long? ExpiresAt { get; init; }

        [JsonPropertyName("user")]
        public AuthUser? User { get; init; }
    }

    private sealed class AuthUser
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = "";

        [JsonPropertyName("email")]
        public string? Email { get; init; }
    }
}
