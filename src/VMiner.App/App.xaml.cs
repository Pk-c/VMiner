using System.Windows;
using System.IO;
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
            var vocabularyTestPath = Path.GetFullPath(reportPath) + ".vocabulary-test.json";
            try
            {
                using var analysis = new AnalysisService(new AppConfig());
                report = await analysis.SelfCheckAsync();

                var vocabulary = new VocabularyStore(vocabularyTestPath);
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
            }
            catch (Exception exception)
            {
                exitCode = 1;
                report = exception.ToString();
            }
            finally
            {
                if (File.Exists(vocabularyTestPath))
                    File.Delete(vocabularyTestPath);
                if (File.Exists(vocabularyTestPath + ".tmp"))
                    File.Delete(vocabularyTestPath + ".tmp");
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
                report = $"Collection UI OK ({itemCount} visible entries) | " +
                         $"mining GIF OK ({miningAnimation.FrameCount} frames)";
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

        MainWindow = new MainWindow();
        MainWindow.Show();
        MainWindow.Activate();
    }
}
