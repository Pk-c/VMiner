using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Util.Store;

namespace VMiner.Services;

internal sealed class EncryptedFileDataStore : IDataStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VMiner.GoogleAuth.v1");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;

    public EncryptedFileDataStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VMiner",
            "GoogleAuth");
    }

    public bool HasStoredCredential => Directory.Exists(_directory) &&
                                       Directory.EnumerateFiles(_directory, "*.auth").Any();

    public async Task StoreAsync<T>(string key, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var serialized = JsonSerializer.SerializeToUtf8Bytes(value);
            var encrypted = ProtectedData.Protect(
                serialized, Entropy, DataProtectionScope.CurrentUser);
            var path = GetPath(key);
            var temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, encrypted).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync<T>(string key)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = GetPath(key);
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = GetPath(key);
            if (!File.Exists(path))
                return default;

            var encrypted = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var serialized = ProtectedData.Unprotect(
                encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<T>(serialized);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_directory))
                return;
            foreach (var file in Directory.EnumerateFiles(_directory, "*.auth"))
                File.Delete(file);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_directory, hash + ".auth");
    }
}
