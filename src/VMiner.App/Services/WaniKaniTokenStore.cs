using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VMiner.Services;

internal interface IWaniKaniTokenStore
{
    bool HasToken(string userId);
    Task<string?> LoadAsync(string userId, CancellationToken cancellationToken = default);
    Task SaveAsync(string userId, string token, CancellationToken cancellationToken = default);
    Task ClearAsync(string userId, CancellationToken cancellationToken = default);
}

internal sealed class EncryptedWaniKaniTokenStore : IWaniKaniTokenStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;

    public EncryptedWaniKaniTokenStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VMiner",
            "WaniKani");
    }

    public bool HasToken(string userId) => File.Exists(GetPath(userId));

    public async Task<string?> LoadAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetPath(userId);
            if (!File.Exists(path))
                return null;
            var encrypted = await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);
            var clear = ProtectedData.Unprotect(
                encrypted, GetEntropy(userId), DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(clear);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        string userId,
        string token,
        CancellationToken cancellationToken = default)
    {
        token = token.Trim();
        if (token.Length == 0)
            throw new ArgumentException("The WaniKani token cannot be empty.", nameof(token));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetPath(userId);
            Directory.CreateDirectory(_directory);
            var clear = Encoding.UTF8.GetBytes(token);
            byte[] encrypted;
            try
            {
                encrypted = ProtectedData.Protect(
                    clear, GetEntropy(userId), DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
            var temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetPath(userId);
            if (File.Exists(path))
                File.Delete(path);
            if (File.Exists(path + ".tmp"))
                File.Delete(path + ".tmp");
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath(string userId)
    {
        EnsureUserId(userId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(userId.Trim()));
        return Path.Combine(_directory, Convert.ToHexString(hash) + ".auth");
    }

    private static byte[] GetEntropy(string userId)
    {
        EnsureUserId(userId);
        return SHA256.HashData(Encoding.UTF8.GetBytes(
            "VMiner.WaniKani.v1:" + userId.Trim()));
    }

    private static void EnsureUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("A VMiner user ID is required.", nameof(userId));
    }
}

internal sealed class MemoryWaniKaniTokenStore : IWaniKaniTokenStore
{
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    public bool HasToken(string userId) => _tokens.ContainsKey(userId);

    public Task<string?> LoadAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_tokens.GetValueOrDefault(userId));
    }

    public Task SaveAsync(
        string userId,
        string token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tokens[userId] = token;
        return Task.CompletedTask;
    }

    public Task ClearAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tokens.Remove(userId);
        return Task.CompletedTask;
    }
}
