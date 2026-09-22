using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using VMiner.Models;
using VMiner.Services;

namespace VMiner.Windows;

public partial class WaniKaniImportWindow : Window
{
    private readonly VocabularyStore _store;
    private readonly IWaniKaniTokenStore _tokenStore;
    private readonly string _userId;
    private CancellationTokenSource? _cancellation;
    private bool _importing;
    private bool _completed;

    internal WaniKaniImportWindow(
        VocabularyStore store,
        IWaniKaniTokenStore tokenStore,
        string userId)
    {
        InitializeComponent();
        _store = store;
        _tokenStore = tokenStore;
        _userId = userId;
        SourceInitialized += (_, _) => NativeMethods.EnableDarkTitleBar(
            new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => TokenBox.Focus();
        Closing += WindowClosing;
    }

    internal VocabularyImportResult? ImportResult { get; private set; }
    internal string? Username { get; private set; }

    private void TokenChanged(object sender, RoutedEventArgs e)
    {
        ImportButton.IsEnabled = !_importing && TokenBox.Password.Trim().Length > 0;
        if (!_importing && !_completed)
        {
            ImportStatus.Text = ImportButton.IsEnabled
                ? "Ready to validate the token and import your studied vocabulary."
                : "Enter your token to enable the import button.";
        }
    }

    private void OpenTokenPageClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                "https://www.wanikani.com/settings/personal_access_tokens")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            ImportStatus.Text = "Could not open the WaniKani token page: " + exception.Message;
        }
    }

    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        if (token.Length == 0)
        {
            ImportStatus.Text = "Enter a WaniKani API token.";
            return;
        }

        _importing = true;
        _cancellation = new CancellationTokenSource();
        ImportButton.IsEnabled = false;
        TokenBox.IsEnabled = false;
        CloseButton.Content = "Cancel import";
        ImportProgress.Visibility = Visibility.Visible;
        ImportProgress.IsIndeterminate = true;
        var progress = new Progress<WaniKaniImportProgress>(UpdateProgress);

        try
        {
            WaniKaniFetchResult fetched;
            using (var service = new WaniKaniImportService())
            {
                fetched = await service.FetchStudiedVocabularyAsync(
                    token, progress, _cancellation.Token);
            }

            var sentenceCount = fetched.Entries.Sum(entry => entry.Examples.Count);
            if (fetched.Entries.Count == 0)
            {
                await _tokenStore.SaveAsync(_userId, token, _cancellation.Token);
                Username = fetched.Username;
                _completed = true;
                ImportProgress.IsIndeterminate = false;
                ImportProgress.Value = 100;
                ImportStatus.Text =
                    $"Setup complete for {fetched.Username}. No studied vocabulary was found yet.";
                CloseButton.Content = "Done";
                return;
            }

            var confirmation = MessageBox.Show(this,
                $"WaniKani account: {fetched.Username}\n\n" +
                $"{fetched.Entries.Count:N0} studied vocabulary entries\n" +
                $"{sentenceCount:N0} context sentences\n\n" +
                "Import and merge these items into your VMiner collection?",
                "Confirm WaniKani import",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes)
            {
                ImportStatus.Text = "Import cancelled. No vocabulary was changed.";
                return;
            }

            ImportResult = await _store.ImportWaniKaniAsync(
                fetched.Entries, progress, _cancellation.Token);
            await _tokenStore.SaveAsync(_userId, token, _cancellation.Token);
            Username = fetched.Username;
            _completed = true;
            ImportProgress.IsIndeterminate = false;
            ImportProgress.Value = 100;
            ImportStatus.Text =
                $"Setup complete: {ImportResult.NewEntries:N0} new words, " +
                $"{ImportResult.NewExamples:N0} new examples, " +
                $"{ImportResult.ExistingEntries:N0} existing words preserved.";
            CloseButton.Content = "Done";
        }
        catch (OperationCanceledException)
        {
            ImportStatus.Text = "WaniKani import cancelled.";
        }
        catch (Exception exception)
        {
            ImportStatus.Text = exception.Message;
        }
        finally
        {
            token = "";
            TokenBox.Clear();
            _cancellation?.Dispose();
            _cancellation = null;
            _importing = false;
            ImportProgress.Visibility = Visibility.Collapsed;
            ImportProgress.IsIndeterminate = false;
            ImportProgress.Value = 0;
            if (!_completed)
            {
                TokenBox.IsEnabled = true;
                ImportButton.IsEnabled = TokenBox.Password.Trim().Length > 0;
                CloseButton.Content = "Close";
            }
        }
    }

    private void UpdateProgress(WaniKaniImportProgress progress)
    {
        ImportStatus.Text = progress.Message;
        if (progress.Total is > 0)
        {
            ImportProgress.IsIndeterminate = false;
            ImportProgress.Maximum = progress.Total.Value;
            ImportProgress.Value = Math.Min(progress.Completed, progress.Total.Value);
        }
        else
        {
            ImportProgress.IsIndeterminate = true;
        }
    }

    private void CloseClicked(object sender, RoutedEventArgs e)
    {
        if (_importing)
            _cancellation?.Cancel();
        else if (_completed)
            DialogResult = true;
        else
            Close();
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_importing)
            _cancellation?.Cancel();
    }
}
