using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace VMiner.Services;

public sealed record ModelDownloadProgress(
    long BytesReceived,
    long TotalBytes,
    double BytesPerSecond,
    bool IsVerifying = false)
{
    public double Percentage => TotalBytes <= 0
        ? 0
        : Math.Clamp(BytesReceived * 100d / TotalBytes, 0, 100);
}

public sealed class ModelDownloadService
{
    public const long ExpectedFileSize = 2_489_909_312;
    public const string ExpectedSha256 =
        "7F7357C14ABD9DA4EB200B38B05DA502CD6E10D7E1D403FBC9F78C19F3209B72";
    public const string SourcePage =
        "https://huggingface.co/tatsuyaaaaaaa/translategemma-4b-it-gguf";
    public const string LicensePage = "https://ai.google.dev/gemma/terms";

    private static readonly Uri DefaultSource = new(
        "https://huggingface.co/tatsuyaaaaaaa/translategemma-4b-it-gguf/resolve/" +
        "main/translategemma-4b-it_Q4_K_M.gguf?download=true");

    private readonly HttpClient _httpClient;
    private readonly Uri _source;
    private readonly long _expectedFileSize;
    private readonly string _expectedSha256;

    public ModelDownloadService()
        : this(CreateHttpClient(), DefaultSource, ExpectedFileSize, ExpectedSha256)
    {
    }

    internal ModelDownloadService(
        HttpClient httpClient,
        Uri source,
        long expectedFileSize,
        string expectedSha256)
    {
        _httpClient = httpClient;
        _source = source;
        _expectedFileSize = expectedFileSize;
        _expectedSha256 = expectedSha256;
    }

    public async Task DownloadAsync(
        string destinationPath,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
                        ?? throw new InvalidOperationException(
                            "The translation model path has no parent directory.");
        Directory.CreateDirectory(directory);

        if (File.Exists(destination))
            return;

        var partialPath = destination + ".download";
        var existingLength = File.Exists(partialPath)
            ? new FileInfo(partialPath).Length
            : 0;
        if (existingLength > _expectedFileSize)
        {
            File.Delete(partialPath);
            existingLength = 0;
        }

        EnsureFreeSpace(directory, _expectedFileSize - existingLength);
        if (existingLength < _expectedFileSize)
        {
            existingLength = await DownloadContentAsync(
                partialPath, existingLength, progress, cancellationToken).ConfigureAwait(false);
        }

        if (existingLength != _expectedFileSize)
            throw new InvalidDataException(
                $"The downloaded model has an unexpected size ({existingLength:N0} bytes). " +
                $"Expected {_expectedFileSize:N0} bytes.");

        progress?.Report(new ModelDownloadProgress(
            existingLength, _expectedFileSize, 0, IsVerifying: true));
        await VerifyChecksumAsync(partialPath, cancellationToken).ConfigureAwait(false);
        File.Move(partialPath, destination, true);
    }

    private async Task<long> DownloadContentAsync(
        string partialPath,
        long existingLength,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _source);
        if (existingLength > 0)
            request.Headers.Range = new RangeHeaderValue(existingLength, null);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var isResume = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!isResume)
            existingLength = 0;

        var totalBytes = response.Content.Headers.ContentRange?.Length
                         ?? (response.Content.Headers.ContentLength is { } contentLength
                             ? existingLength + contentLength
                             : _expectedFileSize);
        if (totalBytes != _expectedFileSize)
            throw new InvalidDataException(
                $"The model host reported an unexpected file size ({totalBytes:N0} bytes). " +
                $"Expected {_expectedFileSize:N0} bytes.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = new FileStream(
            partialPath,
            isResume ? FileMode.OpenOrCreate : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        output.Position = existingLength;

        var received = existingLength;
        var intervalBytes = 0L;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        progress?.Report(new ModelDownloadProgress(received, totalBytes, 0));

        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            received += read;
            intervalBytes += read;

            var elapsedSinceReport = stopwatch.Elapsed - lastReport;
            if (elapsedSinceReport < TimeSpan.FromMilliseconds(150))
                continue;

            var speed = intervalBytes / Math.Max(elapsedSinceReport.TotalSeconds, 0.001);
            progress?.Report(new ModelDownloadProgress(received, totalBytes, speed));
            intervalBytes = 0;
            lastReport = stopwatch.Elapsed;
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new ModelDownloadProgress(received, totalBytes, 0));
        return received;
    }

    private async Task VerifyChecksumAsync(
        string partialPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            partialPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (hash.Equals(_expectedSha256, StringComparison.OrdinalIgnoreCase))
            return;

        stream.Close();
        File.Delete(partialPath);
        throw new InvalidDataException(
            "The downloaded model failed its SHA-256 integrity check. Please try again.");
    }

    private static void EnsureFreeSpace(string directory, long bytesRequired)
    {
        if (bytesRequired <= 0)
            return;

        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (string.IsNullOrWhiteSpace(root))
            return;

        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace >= bytesRequired)
            return;

        throw new IOException(
            $"Not enough free disk space. The model still needs " +
            $"{FormatBytes(bytesRequired)}, but only {FormatBytes(drive.AvailableFreeSpace)} " +
            "is available.");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "VMiner/2.0 (+https://github.com/Pk-c/VMiner)");
        return client;
    }

    internal static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
