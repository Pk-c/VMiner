using System.IO;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using VMiner.Models;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace VMiner.Services;

internal sealed class GoogleDriveVocabularyBackend : IVocabularyDatabaseBackend, IDisposable
{
    private const string DatabaseFileName = "vminer-vocabulary.json";
    private const string DatabaseMimeType = "application/json";
    private const string UserId = "vminer-user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly EncryptedFileDataStore _tokenStore = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private DriveService? _drive;
    private UserCredential? _credential;
    private string? _fileId;

    public string ClientSecretsPath { get; } = Path.Combine(
        AppContext.BaseDirectory, "google-oauth-client.json");

    public bool OAuthConfigured => File.Exists(ClientSecretsPath);
    public bool HasStoredCredential => _tokenStore.HasStoredCredential;
    public bool IsConnected => _drive is not null;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!OAuthConfigured)
            throw new InvalidOperationException(
                "Google OAuth is not configured. Place google-oauth-client.json next to VMiner.exe.");

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_drive is not null)
                return;

            await using var secretsStream = File.OpenRead(ClientSecretsPath);
            var secrets = (await GoogleClientSecrets.FromStreamAsync(
                secretsStream, cancellationToken).ConfigureAwait(false)).Secrets;
            _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                [DriveService.Scope.DriveAppdata],
                UserId,
                cancellationToken,
                _tokenStore).ConfigureAwait(false);
            _drive = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = _credential,
                ApplicationName = "VMiner",
            });
            _fileId = await FindDatabaseFileAsync(cancellationToken).ConfigureAwait(false);
            if (_fileId is null)
                await CreateDatabaseFileAsync(new VocabularyDatabase(), cancellationToken)
                    .ConfigureAwait(false);
        }
        catch
        {
            _drive?.Dispose();
            _drive = null;
            _credential = null;
            _fileId = null;
            throw;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_credential is not null)
            {
                try
                {
                    await _credential.RevokeTokenAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Clearing the local credential still signs VMiner out on this computer.
                }
            }

            await _tokenStore.ClearAsync().ConfigureAwait(false);
            _drive?.Dispose();
            _drive = null;
            _credential = null;
            _fileId = null;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

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
        var drive = GetDrive();
        _fileId ??= await FindDatabaseFileAsync(cancellationToken).ConfigureAwait(false);
        if (_fileId is null)
        {
            var empty = new VocabularyDatabase();
            await CreateDatabaseFileAsync(empty, cancellationToken).ConfigureAwait(false);
            return empty;
        }

        using var content = new MemoryStream();
        var progress = await drive.Files.Get(_fileId)
            .DownloadAsync(content, cancellationToken).ConfigureAwait(false);
        if (progress.Status != Google.Apis.Download.DownloadStatus.Completed)
            throw progress.Exception ?? new IOException(
                "The vocabulary database could not be downloaded from Google Drive.");
        if (content.Length == 0)
            return new VocabularyDatabase();

        content.Position = 0;
        return await JsonSerializer.DeserializeAsync<VocabularyDatabase>(
                   content, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? new VocabularyDatabase();
    }

    public async Task SaveAsync(
        VocabularyDatabase database,
        CancellationToken cancellationToken = default)
    {
        GetDrive();
        using var content = new MemoryStream();
        await JsonSerializer.SerializeAsync(
            content, database, JsonOptions, cancellationToken).ConfigureAwait(false);
        content.Position = 0;

        if (_fileId is null)
        {
            await CreateDatabaseFileAsync(database, cancellationToken).ConfigureAwait(false);
            return;
        }

        var request = _drive!.Files.Update(
            new DriveFile(), _fileId, content, DatabaseMimeType);
        var progress = await request.UploadAsync(cancellationToken).ConfigureAwait(false);
        if (progress.Status != UploadStatus.Completed)
            throw progress.Exception ?? new IOException(
                "The vocabulary database could not be saved to Google Drive.");
    }

    private async Task<string?> FindDatabaseFileAsync(CancellationToken cancellationToken)
    {
        var request = GetDrive().Files.List();
        request.Spaces = "appDataFolder";
        request.Q = $"name = '{DatabaseFileName}'";
        request.Fields = "files(id,name,modifiedTime)";
        request.PageSize = 10;
        var result = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return result.Files?
            .OrderByDescending(file => file.ModifiedTimeDateTimeOffset)
            .FirstOrDefault()?.Id;
    }

    private async Task CreateDatabaseFileAsync(
        VocabularyDatabase database,
        CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        await JsonSerializer.SerializeAsync(
            content, database, JsonOptions, cancellationToken).ConfigureAwait(false);
        content.Position = 0;
        var metadata = new DriveFile
        {
            Name = DatabaseFileName,
            Parents = ["appDataFolder"],
        };
        var request = GetDrive().Files.Create(metadata, content, DatabaseMimeType);
        request.Fields = "id";
        var progress = await request.UploadAsync(cancellationToken).ConfigureAwait(false);
        if (progress.Status != UploadStatus.Completed)
            throw progress.Exception ?? new IOException(
                "The vocabulary database could not be created in Google Drive.");
        _fileId = request.ResponseBody?.Id
                  ?? throw new IOException("Google Drive did not return a database file ID.");
    }

    private DriveService GetDrive() => _drive
        ?? throw new InvalidOperationException(
            "Connect Google Drive before using the vocabulary collection.");

    public void Dispose()
    {
        _drive?.Dispose();
        _drive = null;
    }
}
