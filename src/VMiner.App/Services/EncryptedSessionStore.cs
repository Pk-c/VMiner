using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VMiner.Services;

internal interface ISupabaseSessionStore
{
    bool HasSession { get; }
    Task<SupabaseSession?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SupabaseSession session, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

internal sealed class EncryptedSessionStore : ISupabaseSessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VMiner.SupabaseAuth.v1");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public EncryptedSessionStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VMiner",
            "SupabaseAuth");
        _path = Path.Combine(directory, "session.auth");
    }

    public bool HasSession => File.Exists(_path);

    public async Task<SupabaseSession?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return null;

            var encrypted = await File.ReadAllBytesAsync(_path, cancellationToken)
                .ConfigureAwait(false);
            var serialized = ProtectedData.Unprotect(
                encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<SupabaseSession>(serialized);
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
        SupabaseSession session,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var serialized = JsonSerializer.SerializeToUtf8Bytes(session);
            var encrypted = ProtectedData.Protect(
                serialized, Entropy, DataProtectionScope.CurrentUser);
            var temporary = _path + ".tmp";
            await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, _path, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
            if (File.Exists(_path + ".tmp"))
                File.Delete(_path + ".tmp");
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal sealed class MemorySupabaseSessionStore : ISupabaseSessionStore
{
    private SupabaseSession? _session;

    public bool HasSession => _session is not null;

    public Task<SupabaseSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_session);
    }

    public Task SaveAsync(
        SupabaseSession session,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _session = session;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _session = null;
        return Task.CompletedTask;
    }
}
