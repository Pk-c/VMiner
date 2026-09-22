using System.Windows;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VMiner.Models;
using VMiner.Services;
using VMiner.Windows;

namespace VMiner;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        NativeMethods.EnablePerMonitorDpiAwareness();
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (e.Args is ["--self-test", var reportPath])
        {
            var exitCode = 0;
            string report;
            try
            {
                using var analysis = new AnalysisService(new AppConfig());
                report = await analysis.SelfCheckAsync();

                var vocabulary = new VocabularyStore(
                    new MemoryVocabularyDatabaseBackend());
                await vocabulary.InitializeAsync();
                await vocabulary.AddOrUpdateAsync(
                    "天気", "てんき", "weather",
                    "今日はいい天気ですね。", "The weather is lovely today.");
                await vocabulary.AddOrUpdateAsync(
                    "天気", "てんき", "weather",
                    "今日はいい天気ですね。", "The weather is lovely today.");
                var savedEntry = await vocabulary.FindAsync("天気", "てんき")
                                 ?? throw new InvalidOperationException(
                                     "Vocabulary database test entry was not saved.");
                if (savedEntry.Examples.Count != 1)
                    throw new InvalidOperationException(
                        "Duplicate sentence example was not rejected.");
                await vocabulary.UpdateAsync("天気", "てんき", new VocabularyEntry
                {
                    Word = "天気",
                    Reading = "てんき",
                    Definition = "weather; atmospheric conditions",
                    Examples = savedEntry.Examples,
                });
                var entries = await vocabulary.GetAllAsync();
                if (entries.Count != 1 || entries[0].Definition != "weather; atmospheric conditions")
                    throw new InvalidOperationException("Vocabulary database update test failed.");
                await vocabulary.DeleteAsync("天気", "てんき");
                if (await vocabulary.FindAsync("天気", "てんき") is not null)
                    throw new InvalidOperationException("Vocabulary database delete test failed.");
                report += " | database CRUD OK";

                await TestSupabaseClientAsync();
                report += " | Supabase session + database client OK";
            }
            catch (Exception exception)
            {
                exitCode = 1;
                report = exception.ToString();
            }
            File.WriteAllText(Path.GetFullPath(reportPath), report);
            Shutdown(exitCode);
            return;
        }

        if (e.Args is ["--collection-ui-test", var collectionReportPath])
        {
            var exitCode = 0;
            MainWindow? testWindow = null;
            string report;
            try
            {
                testWindow = new MainWindow(testMode: true);
                MainWindow = testWindow;
                testWindow.Show();
                var itemCount = await testWindow.TestCollectionTabAsync();
                var miningAnimation = testWindow.TestMiningAnimation();
                if (miningAnimation.FrameCount < 2 || !miningAnimation.IsPlaying)
                    throw new InvalidOperationException(
                        "The mining animation was not loaded or did not start playing.");

                using var testAuth = new SupabaseAuthService();
                var accountWindow = new AccountWindow(testAuth) { Owner = testWindow };
                accountWindow.Show();
                try
                {
                    accountWindow.UpdateLayout();
                    if (!accountWindow.IsVisible)
                        throw new InvalidOperationException(
                            "The account sign-in window did not open.");
                }
                finally
                {
                    accountWindow.Close();
                }
                report = $"Collection UI OK ({itemCount} visible entries) | " +
                         $"mining GIF OK ({miningAnimation.FrameCount} frames) | " +
                         "account UI OK";
            }
            catch (Exception exception)
            {
                exitCode = 1;
                report = exception.ToString();
            }

            File.WriteAllText(Path.GetFullPath(collectionReportPath), report);
            if (testWindow is not null)
                testWindow.Close();
            else
                Shutdown(exitCode);
            return;
        }

        if (e.Args is ["--missing-model-ui-test", var missingModelReportPath])
        {
            var exitCode = 0;
            MainWindow? testWindow = null;
            string report;
            try
            {
                testWindow = new MainWindow(
                    testMode: true,
                    translationModelOverride: @"models\missing-model-ui-test.gguf");
                MainWindow = testWindow;
                testWindow.Show();
                var state = testWindow.TestMissingModelPanel();
                if (!state.IsVisible || !state.CanDownload ||
                    !state.Status.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The missing-model warning or download action is not visible.");
                }
                report = "Missing-model UI OK (warning, download button, and progress bar visible)";
            }
            catch (Exception exception)
            {
                exitCode = 1;
                report = exception.ToString();
            }

            File.WriteAllText(Path.GetFullPath(missingModelReportPath), report);
            if (testWindow is not null)
                testWindow.Close();
            else
                Shutdown(exitCode);
            return;
        }

        if (e.Args is ["--model-download-test", var downloadReportPath])
        {
            var exitCode = 0;
            string report;
            var destination = Path.GetFullPath(downloadReportPath) + ".download-test.gguf";
            var partial = destination + ".download";
            try
            {
                var payload = Encoding.UTF8.GetBytes(string.Concat(
                    Enumerable.Repeat("VMiner model download integrity test.\n", 65_536)));
                var expectedHash = Convert.ToHexString(SHA256.HashData(payload));
                await File.WriteAllBytesAsync(partial, payload[..131_072]);

                var handler = new ModelDownloadTestHandler(payload);
                using var client = new HttpClient(handler);
                var downloader = new ModelDownloadService(
                    client,
                    new Uri("https://download.test/model.gguf"),
                    payload.LongLength,
                    expectedHash);
                var progress = new DownloadProgressRecorder();
                await downloader.DownloadAsync(destination, progress);

                var downloaded = await File.ReadAllBytesAsync(destination);
                if (!payload.AsSpan().SequenceEqual(downloaded) ||
                    !handler.ResumeRequested || progress.Latest?.Percentage != 100)
                {
                    throw new InvalidOperationException(
                        "The resumable model download test did not complete correctly.");
                }
                report = $"Model download OK ({downloaded.Length:N0} bytes, resume + SHA-256)";
            }
            catch (Exception exception)
            {
                exitCode = 1;
                report = exception.ToString();
            }
            finally
            {
                if (File.Exists(destination))
                    File.Delete(destination);
                if (File.Exists(partial))
                    File.Delete(partial);
            }

            File.WriteAllText(Path.GetFullPath(downloadReportPath), report);
            Shutdown(exitCode);
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
        MainWindow.Activate();
    }

    private sealed class DownloadProgressRecorder : IProgress<ModelDownloadProgress>
    {
        public ModelDownloadProgress? Latest { get; private set; }

        public void Report(ModelDownloadProgress value) => Latest = value;
    }

    private sealed class ModelDownloadTestHandler(byte[] payload) : HttpMessageHandler
    {
        public bool ResumeRequested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = request.Headers.Range?.Ranges.SingleOrDefault()?.From ?? 0;
            if (start < 0 || start >= payload.LongLength)
                throw new InvalidOperationException("Invalid byte range in download test.");

            ResumeRequested = start > 0;
            var responsePayload = payload.AsSpan((int)start).ToArray();
            var response = new HttpResponseMessage(
                ResumeRequested ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responsePayload),
            };
            response.Content.Headers.ContentLength = responsePayload.LongLength;
            if (ResumeRequested)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    start, payload.LongLength - 1, payload.LongLength);
            }
            return Task.FromResult(response);
        }
    }

    private static async Task TestSupabaseClientAsync()
    {
        var handler = new SupabaseTestHandler();
        using var client = new HttpClient(handler);
        var sessionStore = new MemorySupabaseSessionStore();
        var options = new SupabaseOptions
        {
            Url = "https://vminer-test.supabase.co",
            PublishableKey = "sb_publishable_test",
        };

        using (var auth = new SupabaseAuthService(options, client, sessionStore))
        using (var backend = new SupabaseVocabularyBackend(auth, client))
        {
            await auth.SignInAsync("miner@example.com", "test-password");
            var cloudStore = new VocabularyStore(backend);
            await cloudStore.AddOrUpdateAsync(
                "鉱山", "こうざん", "mine",
                "鉱山で働く。", "Work in a mine.");
            var entry = await cloudStore.FindAsync("鉱山", "こうざん");
            if (entry?.Definition != "mine")
                throw new InvalidOperationException(
                    "Supabase vocabulary round-trip test failed.");
        }

        using (var restoredAuth = new SupabaseAuthService(options, client, sessionStore))
        {
            if (!await restoredAuth.TryRestoreSessionAsync() ||
                !string.Equals(restoredAuth.Email, "miner@example.com",
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Supabase session restore test failed.");
            await restoredAuth.SignOutAsync();
        }

        if (sessionStore.HasSession || !handler.RefreshRequested || !handler.LogoutRequested)
            throw new InvalidOperationException("Supabase session lifecycle test failed.");
    }

    private sealed class SupabaseTestHandler : HttpMessageHandler
    {
        private string? _database;

        public bool RefreshRequested { get; private set; }
        public bool LogoutRequested { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path == "/auth/v1/token?grant_type=password")
                return JsonResponse(CreateSessionJson("access-1", "refresh-1"));
            if (path == "/auth/v1/token?grant_type=refresh_token")
            {
                RefreshRequested = true;
                return JsonResponse(CreateSessionJson("access-2", "refresh-2"));
            }
            if (path == "/auth/v1/logout")
            {
                LogoutRequested = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Get &&
                path.StartsWith("/rest/v1/vocabulary_databases?", StringComparison.Ordinal))
            {
                return JsonResponse(_database is null
                    ? "[]"
                    : $"[{{\"data\":{_database}}}]");
            }
            if (request.Method == HttpMethod.Post &&
                path.StartsWith("/rest/v1/vocabulary_databases?", StringComparison.Ordinal))
            {
                var json = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(json);
                _database = document.RootElement.GetProperty("data").GetRawText();
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"message\":\"Unexpected test request\"}",
                    Encoding.UTF8, "application/json"),
            };
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        private static string CreateSessionJson(string accessToken, string refreshToken) =>
            $$"""
              {
                "access_token": "{{accessToken}}",
                "refresh_token": "{{refreshToken}}",
                "expires_in": 3600,
                "user": {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "email": "miner@example.com"
                }
              }
              """;
    }
}
