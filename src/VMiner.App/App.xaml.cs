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

                await TestWaniKaniClientAsync();
                report += " | WaniKani import client OK";

                var tokenStore = new MemoryWaniKaniTokenStore();
                await tokenStore.SaveAsync("user-a", "test-read-only-token");
                if (!tokenStore.HasToken("user-a") || tokenStore.HasToken("user-b") ||
                    await tokenStore.LoadAsync("user-a") != "test-read-only-token")
                    throw new InvalidOperationException(
                        "WaniKani token store test failed.");
                await tokenStore.ClearAsync("user-a");
                if (tokenStore.HasToken("user-a") ||
                    await tokenStore.LoadAsync("user-a") is not null)
                    throw new InvalidOperationException(
                        "WaniKani token store clear test failed.");
                report += " | WaniKani token store OK";
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
                var importWindow = new WaniKaniImportWindow(new VocabularyStore(
                    new MemoryVocabularyDatabaseBackend()),
                    new MemoryWaniKaniTokenStore(), "ui-test-user") { Owner = testWindow };
                importWindow.Show();
                try
                {
                    importWindow.UpdateLayout();
                    if (!importWindow.IsVisible)
                        throw new InvalidOperationException(
                            "The WaniKani import window did not open.");
                }
                finally
                {
                    importWindow.Close();
                }
                report = $"Collection UI OK ({itemCount} visible entries) | " +
                         $"mining GIF OK ({miningAnimation.FrameCount} frames) | " +
                         "account UI OK | WaniKani import UI OK";
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
        if (new SupabaseOptions
            {
                Url = "https://vminer-test.supabase.co",
                PublishableKey = "sb_secret_must-never-be-accepted",
            }.IsValid)
            throw new InvalidOperationException(
                "Supabase secret-key protection test failed.");

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
            await cloudStore.InitializeAsync();
            await cloudStore.AddOrUpdateAsync(
                "鉱山", "こうざん", "mine",
                "鉱山で働く。", "Work in a mine.");
            await cloudStore.ReloadAsync();
            var entry = await cloudStore.FindAsync("鉱山", "こうざん");
            if (entry?.Definition != "mine" || entry.Examples.Count != 1)
                throw new InvalidOperationException(
                    "Supabase vocabulary round-trip test failed.");
            await cloudStore.UpdateAsync("鉱山", "こうざん", new VocabularyEntry
            {
                Word = "鉱山",
                Reading = "こうざん",
                Definition = "mine; mining site",
                Examples = entry.Examples,
            });
            await cloudStore.DeleteAsync("鉱山", "こうざん");
            await cloudStore.ReloadAsync();
            if (await cloudStore.FindAsync("鉱山", "こうざん") is not null)
                throw new InvalidOperationException(
                    "Supabase vocabulary delete test failed.");
            handler.WaniKaniRpcAvailable = false;
            var imported = await cloudStore.ImportWaniKaniAsync([
                new WaniKaniVocabularyItem(
                    9210,
                    DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
                    "おやつ",
                    "おやつ",
                    "Snack",
                    [new SentencePair
                    {
                        Japanese = "今日はおやつにマフィンを食べた。",
                        English = "Today I had a muffin for a snack.",
                    }])
            ]);
            var importedEntry = await cloudStore.FindAsync("おやつ", "おやつ");
            if (imported.NewEntries != 1 || imported.NewExamples != 1 ||
                importedEntry?.Definition != "Snack" || importedEntry.Examples.Count != 1)
                throw new InvalidOperationException(
                    "Supabase WaniKani compatibility import test failed.");
            var merged = await cloudStore.ImportWaniKaniAsync([
                new WaniKaniVocabularyItem(
                    9210,
                    DateTimeOffset.Parse("2026-02-03T04:05:06Z"),
                    "おやつ",
                    "おやつ",
                    "Between-meal food",
                    [
                        new SentencePair
                        {
                            Japanese = "今日はおやつにマフィンを食べた。",
                            English = "Today I had a muffin for a snack.",
                        },
                        new SentencePair
                        {
                            Japanese = "そろそろおやつにする？",
                            English = "Shall we take a snack break?",
                        },
                    ])
            ]);
            var mergedEntry = await cloudStore.FindAsync("おやつ", "おやつ");
            if (merged.ExistingEntries != 1 || merged.NewExamples != 1 ||
                mergedEntry?.Definition != "Snack" || mergedEntry.Examples.Count != 2)
            {
                throw new InvalidOperationException(
                    "Supabase WaniKani compatibility merge test failed.");
            }
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
        private static readonly Guid EntryId =
            Guid.Parse("22222222-2222-2222-2222-222222222222");
        private static readonly Guid ExampleId =
            Guid.Parse("33333333-3333-3333-3333-333333333333");
        private string? _word;
        private string _reading = "";
        private string _definition = "";
        private readonly List<SentencePair> _examples = [];

        public bool RefreshRequested { get; private set; }
        public bool LogoutRequested { get; private set; }
        public bool WaniKaniRpcAvailable { get; set; } = true;

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
                path.StartsWith("/rest/v1/vocabulary_entries?", StringComparison.Ordinal))
            {
                if (!path.EndsWith("offset=0", StringComparison.Ordinal))
                    return JsonResponse("[]");
                if (_word is null)
                    return JsonResponse("[]");
                return JsonResponse(JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        id = EntryId,
                        word = _word,
                        reading = _reading,
                        definition = _definition,
                        sentence_examples = _examples.Select(example => new
                        {
                            id = example.Id,
                            japanese = example.Japanese,
                            english = example.English,
                        }),
                    },
                }));
            }
            if (request.Method == HttpMethod.Post &&
                path == "/rest/v1/rpc/add_or_update_vocabulary")
            {
                var json = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                _word = root.GetProperty("p_word").GetString();
                _reading = root.GetProperty("p_reading").GetString() ?? "";
                _definition = root.GetProperty("p_definition").GetString() ?? "";
                var japanese = root.GetProperty("p_japanese").GetString() ?? "";
                var english = root.GetProperty("p_english").GetString() ?? "";
                if (japanese.Length > 0 && !_examples.Any(example =>
                        example.Japanese == japanese && example.English == english))
                {
                    _examples.Add(new SentencePair
                    {
                        Id = ExampleId,
                        Japanese = japanese,
                        English = english,
                    });
                }
                return JsonResponse(JsonSerializer.Serialize(EntryId));
            }
            if (request.Method == HttpMethod.Post &&
                path == "/rest/v1/rpc/replace_vocabulary_entry")
            {
                var json = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                _word = root.GetProperty("p_word").GetString();
                _reading = root.GetProperty("p_reading").GetString() ?? "";
                _definition = root.GetProperty("p_definition").GetString() ?? "";
                _examples.Clear();
                foreach (var example in root.GetProperty("p_examples").EnumerateArray())
                {
                    _examples.Add(new SentencePair
                    {
                        Id = Guid.NewGuid(),
                        Japanese = example.GetProperty("japanese").GetString() ?? "",
                        English = example.GetProperty("english").GetString() ?? "",
                    });
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Post &&
                path == "/rest/v1/rpc/import_wanikani_vocabulary")
            {
                if (!WaniKaniRpcAvailable)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent(
                            "{\"code\":\"PGRST202\",\"message\":\"Could not find the " +
                            "function public.import_wanikani_vocabulary(p_entries) in the " +
                            "schema cache\"}", Encoding.UTF8, "application/json"),
                    };
                }
                var json = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(json);
                var entries = document.RootElement.GetProperty("p_entries");
                var newEntries = 0;
                var existingEntries = 0;
                var newExamples = 0;
                foreach (var imported in entries.EnumerateArray())
                {
                    var word = imported.GetProperty("word").GetString() ?? "";
                    var reading = imported.GetProperty("reading").GetString() ?? "";
                    if (_word is null)
                    {
                        _word = word;
                        _reading = reading;
                        _definition = imported.GetProperty("definition").GetString() ?? "";
                        newEntries++;
                    }
                    else
                    {
                        existingEntries++;
                    }
                    foreach (var example in imported.GetProperty("examples").EnumerateArray())
                    {
                        var japanese = example.GetProperty("japanese").GetString() ?? "";
                        var english = example.GetProperty("english").GetString() ?? "";
                        if (_examples.Any(item => item.Japanese == japanese && item.English == english))
                            continue;
                        _examples.Add(new SentencePair
                        {
                            Id = ExampleId,
                            Japanese = japanese,
                            English = english,
                        });
                        newExamples++;
                    }
                }
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    new_entries = newEntries,
                    existing_entries = existingEntries,
                    new_examples = newExamples,
                }));
            }
            if (request.Method == HttpMethod.Delete &&
                path.StartsWith("/rest/v1/vocabulary_entries?id=eq.", StringComparison.Ordinal))
            {
                _word = null;
                _examples.Clear();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
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

    private static async Task TestWaniKaniClientAsync()
    {
        var handler = new WaniKaniTestHandler();
        using var client = new HttpClient(handler);
        using var service = new WaniKaniImportService(client);
        var result = await service.FetchStudiedVocabularyAsync("read-only-test-token");
        var kanaEntry = result.Entries.SingleOrDefault(entry => entry.SubjectId == 9210);
        var kanjiEntry = result.Entries.SingleOrDefault(entry => entry.SubjectId == 2467);
        if (result.Username != "miner" || result.Entries.Count != 2 ||
            kanaEntry is null || kanaEntry.Word != "おやつ" ||
            kanaEntry.Reading != "おやつ" || kanaEntry.Definition != "Snack" ||
            kanaEntry.Examples.Count != 1 || kanjiEntry is null ||
            kanjiEntry.Word != "一" || kanjiEntry.Reading != "いち" ||
            kanjiEntry.Definition != "One" || !handler.AuthenticationVerified)
        {
            throw new InvalidOperationException("WaniKani import mapping test failed.");
        }
    }

    private sealed class WaniKaniTestHandler : HttpMessageHandler
    {
        public bool AuthenticationVerified { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AuthenticationVerified =
                request.Headers.Authorization?.Scheme == "Bearer" &&
                request.Headers.Authorization.Parameter == "read-only-test-token" &&
                request.Headers.TryGetValues("Wanikani-Revision", out var revisions) &&
                revisions.Single() == "20170710";
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path == "/v2/user")
            {
                return Task.FromResult(JsonResponse("""
                    {"data":{"username":"miner","subscription":{"max_level_granted":60}}}
                    """));
            }
            if (path.StartsWith("/v2/assignments?", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "pages":{"next_url":null},
                      "data":[
                        {"data":{"subject_id":9210}},
                        {"data":{"subject_id":2467}},
                        {"data":{"subject_id":9999}}
                      ]
                    }
                    """));
            }
            if (path.StartsWith("/v2/subjects?ids=", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "data":[
                        {
                          "id":9210,
                          "object":"kana_vocabulary",
                          "data_updated_at":"2026-01-02T03:04:05Z",
                          "data":{
                            "level":8,
                            "characters":"おやつ",
                            "hidden_at":null,
                            "meanings":[{"meaning":"Snack","primary":true}],
                            "context_sentences":[{
                              "ja":"今日はおやつにマフィンを食べた。",
                              "en":"Today I had a muffin for a snack."
                            }]
                          }
                        },
                        {
                          "id":2467,
                          "object":"vocabulary",
                          "data_updated_at":"2026-01-02T03:04:05Z",
                          "data":{
                            "level":1,
                            "characters":"一",
                            "hidden_at":null,
                            "meanings":[{"meaning":"One","primary":true}],
                            "readings":[{"reading":"いち","primary":true}],
                            "context_sentences":[]
                          }
                        }
                      ]
                    }
                    """));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
