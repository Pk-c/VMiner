using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VMiner.Models;
using VMiner.Services;

namespace VMiner.Windows;

public partial class MainWindow : Window
{
    private readonly ConfigService _configService = new();
    private readonly AppConfig _config;
    private readonly AnalysisService _analysis;
    private readonly SupabaseAuthService _supabaseAuth;
    private readonly SupabaseVocabularyBackend _supabaseVocabulary;
    private readonly VocabularyStore _vocabularyStore;
    private readonly GlobalCaptureService _capture;
    private readonly ResultWindow _resultWindow;
    private readonly ModelDownloadService _modelDownloader = new();
    private readonly bool _testMode;
    private CancellationTokenSource? _analysisCancellation;
    private CancellationTokenSource? _modelDownloadCancellation;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private IReadOnlyList<VocabularyEntry> _collectionEntries = [];
    private int _requestId;
    private bool _reallyClosing;
    private bool _shutdownStarted;
    private bool _modelDownloadInProgress;

    public MainWindow() : this(false)
    {
    }

    internal MainWindow(bool testMode, string? translationModelOverride = null)
    {
        _testMode = testMode;
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.EnableDarkTitleBar(
            new WindowInteropHelper(this).Handle);
        _config = _configService.Load();
        if (!string.IsNullOrWhiteSpace(translationModelOverride))
            _config.TranslationModel = translationModelOverride;
        _analysis = new AnalysisService(_config);
        _supabaseAuth = new SupabaseAuthService();
        _supabaseVocabulary = new SupabaseVocabularyBackend(_supabaseAuth);
        _vocabularyStore = new VocabularyStore(_supabaseVocabulary);
        _resultWindow = new ResultWindow(
            _config, _analysis.Translation, _vocabularyStore);
        _resultWindow.VocabularyChanged += async (_, _) =>
        {
            if (MainTabs.SelectedItem == CollectionTab)
                await RefreshCollectionAsync();
        };
        _capture = new GlobalCaptureService(Dispatcher, () => _config);
        _capture.RegionCaptured += CaptureReceived;
        _capture.CaptureFailed += (_, message) => StatusText.Text = $"Capture failed: {message}";

        LoadConfiguration();
        if (!testMode)
            Loaded += WindowLoaded;
        Closing += WindowClosing;
    }

    internal async Task<int> TestCollectionTabAsync()
    {
        MainTabs.SelectedItem = CollectionTab;
        await RefreshCollectionAsync();
        UpdateLayout();
        return CollectionList.Items.Count;
    }

    internal (int FrameCount, bool IsPlaying) TestMiningAnimation()
        => _resultWindow.TestMiningAnimation();

    internal (bool IsVisible, bool CanDownload, string Status) TestMissingModelPanel()
    {
        UpdateModelStatus();
        UpdateLayout();
        return (
            ModelDownloadPanel.Visibility == Visibility.Visible,
            ModelDownloadButton.IsEnabled,
            ModelStatusText.Text);
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _capture.Start();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            MessageBox.Show(this, exception.Message, "VMiner", MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var modelInitialization = InitializeTranslationModelAsync();
        await RestoreSupabaseSessionAsync();
        await modelInitialization;
    }

    private async Task InitializeTranslationModelAsync()
    {
        if (!_analysis.Translation.ModelAvailable)
        {
            UpdateModelStatus();
            StatusText.Text = "Ready — OCR and furigana are active; TranslateGemma model is missing.";
            return;
        }

        try
        {
            ShowModelLoading();
            StatusText.Text = "Loading TranslateGemma in the background…";
            await _analysis.Translation.InitializeAsync(_lifetimeCancellation.Token);
            if (_reallyClosing)
                return;
            UpdateModelStatus();
            StatusText.Text = "Ready — Japanese capture, mining, and vocabulary tools are active.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowModelError(exception.Message);
        }
    }

    private async void CaptureReceived(object? sender, BitmapSource image)
    {
        CollectConfiguration();
        var requestId = ++_requestId;
        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        var backend = _analysis.Translation.ModelAvailable
            ? "Windows OCR + English TranslateGemma"
            : "OCR Windows";
        StatusText.Text = $"Area {image.PixelWidth}×{image.PixelHeight} — reading text…";
        _resultWindow.ShowPending("Reading Japanese text…");
        var progress = new Progress<string>(message =>
        {
            if (requestId != _requestId)
                return;
            StatusText.Text = message;
            _resultWindow.UpdatePending(message);
        });

        try
        {
            var analysis = await _analysis.AnalyseAsync(
                image, progress, _analysisCancellation.Token);
            if (requestId != _requestId)
                return;
            StatusText.Text = analysis.IsEmpty
                ? "No Japanese text detected."
                : "Results displayed.";
            await _resultWindow.ShowResultAsync(analysis, backend);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (requestId != _requestId)
                return;
            StatusText.Text = $"Error: {exception.Message}";
            _resultWindow.ShowError(exception.Message);
        }
    }

    private void LoadConfiguration()
    {
        foreach (ComboBoxItem item in HotkeyBox.Items)
        {
            if (Equals(item.Tag, _config.Hotkey))
            {
                HotkeyBox.SelectedItem = item;
                break;
            }
        }
        HotkeyBox.SelectedIndex = Math.Max(0, HotkeyBox.SelectedIndex);
        FontSizeBox.Text = _config.FontSize.ToString();
        FocusCheck.IsChecked = _config.StealFocus;
        UpdateModelStatus();
        UpdateVocabularyStatus();
        UpdateHint();
    }

    private void CollectConfiguration()
    {
        if (HotkeyBox.SelectedItem is ComboBoxItem { Tag: string hotkey })
            _config.Hotkey = hotkey;
        if (int.TryParse(FontSizeBox.Text, out var fontSize))
            _config.FontSize = Math.Clamp(fontSize, 12, 48);
        _config.StealFocus = FocusCheck.IsChecked == true;
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        CollectConfiguration();
        _configService.Save(_config);
        StatusText.Text = "Settings saved.";
    }

    private async void AccountClicked(object sender, RoutedEventArgs e)
    {
        if (_supabaseAuth.IsAuthenticated)
            await SignOutAsync();
        else
            await ShowAccountWindowAsync();
    }

    private async Task RestoreSupabaseSessionAsync()
    {
        UpdateVocabularyStatus();
        if (!_supabaseAuth.IsConfigured || !_supabaseAuth.HasStoredSession)
            return;

        AccountButton.IsEnabled = false;
        AccountButton.Content = "Signing in…";
        VocabularyStatusText.Text = "Restoring your VMiner account…";
        try
        {
            if (await _supabaseAuth.TryRestoreSessionAsync(_lifetimeCancellation.Token))
                await FinishSignInAsync();
            else
                StatusText.Text = "Your previous session expired. Sign in again to sync vocabulary.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Automatic sign-in failed: {exception.Message}";
        }
        finally
        {
            UpdateVocabularyStatus();
        }
    }

    private async Task ShowAccountWindowAsync()
    {
        if (!_supabaseAuth.IsConfigured)
            return;

        var dialog = new AccountWindow(_supabaseAuth) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            UpdateVocabularyStatus();
            return;
        }

        await FinishSignInAsync();
    }

    private async Task FinishSignInAsync()
    {
        AccountButton.IsEnabled = false;
        VocabularyStatusText.Text = "Loading your cloud collection…";
        try
        {
            var imported = await _supabaseVocabulary.ImportLocalDatabaseIfEmptyAsync(
                _config.LegacyVocabularyDatabasePath,
                _lifetimeCancellation.Token);
            await _vocabularyStore.InitializeAsync(_lifetimeCancellation.Token);
            _config.LegacyVocabularyDatabasePath = null;
            _configService.Save(_config);
            UpdateVocabularyStatus();
            StatusText.Text = imported
                ? "Signed in — the previous local collection was imported."
                : "Signed in — your vocabulary collection is synchronized.";
            if (MainTabs.SelectedItem == CollectionTab)
                await RefreshCollectionAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            VocabularyStatusText.Text = "The cloud collection could not be loaded";
            MessageBox.Show(this, exception.Message, "VMiner", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            UpdateVocabularyStatus();
        }
    }

    private async Task SignOutAsync()
    {
        var confirmation = MessageBox.Show(this,
            "Sign out of VMiner on this computer? Your vocabulary collection will remain safely stored in your account.",
            "Sign out", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
            return;

        AccountButton.IsEnabled = false;
        AccountButton.Content = "Signing out…";
        try
        {
            await _supabaseAuth.SignOutAsync(_lifetimeCancellation.Token);
            _collectionEntries = [];
            CollectionList.ItemsSource = null;
            CollectionStatusText.Text = "Sign in from Capture & model to access your collection.";
            StatusText.Text = "Signed out. Your cloud collection was not deleted.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "VMiner", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            UpdateVocabularyStatus();
        }
    }

    private void UpdateVocabularyStatus()
    {
        if (!_supabaseAuth.IsConfigured)
        {
            VocabularyStatusText.Text = "Supabase setup is missing";
            VocabularyStatusText.ToolTip =
                $"Place supabase-config.json next to VMiner.exe:\n{SupabaseAuthService.ConfigurationPath}";
            AccountButton.Content = "Setup required";
            AccountButton.IsEnabled = false;
            return;
        }

        VocabularyStatusText.ToolTip = "Your vocabulary is private to your VMiner account.";
        AccountButton.IsEnabled = true;
        if (_supabaseAuth.IsAuthenticated)
        {
            VocabularyStatusText.Text = string.IsNullOrWhiteSpace(_supabaseAuth.Email)
                ? "Signed in • collection synchronized"
                : $"Signed in as {_supabaseAuth.Email}";
            AccountButton.Content = "Log out";
        }
        else
        {
            VocabularyStatusText.Text = "Not signed in — connect or create an account";
            AccountButton.Content = "Sign in";
        }
    }

    private async void MainTabsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || e.Source != MainTabs || MainTabs.SelectedItem != CollectionTab)
            return;
        await RefreshCollectionAsync();
    }

    private async void RefreshCollectionClicked(object sender, RoutedEventArgs e) =>
        await RefreshCollectionAsync();

    private async Task RefreshCollectionAsync()
    {
        EditEntryButton.IsEnabled = false;
        RemoveEntryButton.IsEnabled = false;
        if (!_vocabularyStore.IsConfigured)
        {
            _collectionEntries = [];
            CollectionList.ItemsSource = null;
            CollectionStatusText.Text = "Sign in from Capture & model to access your collection.";
            return;
        }

        try
        {
            CollectionStatusText.Text = "Loading collection…";
            _collectionEntries = await _vocabularyStore.GetAllAsync();
            ApplyCollectionFilter();
        }
        catch (Exception exception)
        {
            CollectionList.ItemsSource = null;
            CollectionStatusText.Text = $"Collection could not be loaded: {exception.Message}";
        }
    }

    private void CollectionSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        ApplyCollectionFilter();
    }

    private void ApplyCollectionFilter()
    {
        var query = CollectionSearchBox.Text.Trim();
        var filtered = query.Length == 0
            ? _collectionEntries
            : _collectionEntries.Where(entry =>
                    Contains(entry.Word, query) ||
                    Contains(entry.Reading, query) ||
                    Contains(entry.Definition, query) ||
                    entry.Examples.Any(example =>
                        Contains(example.Japanese, query) || Contains(example.English, query)))
                .ToArray();
        CollectionList.ItemsSource = filtered;
        CollectionStatusText.Text = filtered.Count == _collectionEntries.Count
            ? $"{filtered.Count} entries"
            : $"{filtered.Count} of {_collectionEntries.Count} entries";
    }

    private static bool Contains(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void CollectionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = CollectionList.SelectedItem is VocabularyEntry;
        EditEntryButton.IsEnabled = selected;
        RemoveEntryButton.IsEnabled = selected;
    }

    private async void EditEntryClicked(object sender, RoutedEventArgs e) =>
        await EditSelectedEntryAsync();

    private async void CollectionDoubleClicked(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        await EditSelectedEntryAsync();

    private async Task EditSelectedEntryAsync()
    {
        if (CollectionList.SelectedItem is not VocabularyEntry entry)
            return;

        var dialog = new EditVocabularyWindow(entry, _vocabularyStore) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            StatusText.Text = $"{entry.Word} updated.";
            await RefreshCollectionAsync();
        }
    }

    private async void RemoveEntryClicked(object sender, RoutedEventArgs e)
    {
        if (CollectionList.SelectedItem is not VocabularyEntry entry)
            return;
        var confirmation = MessageBox.Show(this,
            $"Remove {entry.Word} ({entry.Reading}) and all of its sentence examples?",
            "Remove vocabulary entry", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        try
        {
            await _vocabularyStore.DeleteAsync(entry.Word, entry.Reading);
            StatusText.Text = $"{entry.Word} removed from the collection.";
            await RefreshCollectionAsync();
        }
        catch (Exception exception)
        {
            CollectionStatusText.Text = exception.Message;
        }
    }

    private void HotkeyChanged(object sender, SelectionChangedEventArgs e) => UpdateHint();

    private void UpdateModelStatus()
    {
        var available = _analysis.Translation.ModelAvailable;
        if (!available)
        {
            ModelStatusText.Text =
                "Translation unavailable — the TranslateGemma model is not installed.";
            ModelStatusIndicator.Fill = (Brush)FindResource("DangerBrush");
            ModelDownloadPanel.Visibility = Visibility.Visible;
            if (!_modelDownloadInProgress)
            {
                var partialPath = _analysis.Translation.ModelPath + ".download";
                var partialBytes = File.Exists(partialPath)
                    ? Math.Min(new FileInfo(partialPath).Length,
                        ModelDownloadService.ExpectedFileSize)
                    : 0;
                ModelDownloadProgress.IsIndeterminate = false;
                ModelDownloadProgress.Value = partialBytes * 100d /
                                              ModelDownloadService.ExpectedFileSize;
                ModelDownloadButton.Content = partialBytes > 0
                    ? "Resume download"
                    : "Download model";
                ModelDownloadButton.IsEnabled = true;
                ModelDownloadProgressText.Text = partialBytes > 0
                    ? $"{ModelDownloadService.FormatBytes(partialBytes)} / " +
                      $"{ModelDownloadService.FormatBytes(
                          ModelDownloadService.ExpectedFileSize)} saved"
                    : $"Download size: {ModelDownloadService.FormatBytes(
                        ModelDownloadService.ExpectedFileSize)}";
            }
            return;
        }

        ModelDownloadPanel.Visibility = Visibility.Collapsed;
        if (!_analysis.Translation.IsLoaded)
        {
            ShowModelLoading();
            return;
        }

        ModelStatusText.Text = "TranslateGemma ready • Japanese → English";
        ModelStatusIndicator.Fill = (Brush)FindResource("SuccessBrush");
    }

    private async void DownloadModelClicked(object sender, RoutedEventArgs e)
    {
        if (_modelDownloadInProgress)
        {
            ModelDownloadButton.IsEnabled = false;
            ModelDownloadButton.Content = "Cancelling…";
            _modelDownloadCancellation?.Cancel();
            return;
        }

        if (_analysis.Translation.ModelAvailable)
        {
            await InitializeTranslationModelAsync();
            return;
        }

        _modelDownloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _modelDownloadInProgress = true;
        ModelDownloadPanel.Visibility = Visibility.Visible;
        ModelDownloadButton.Content = "Cancel download";
        ModelDownloadButton.IsEnabled = true;
        ModelDownloadProgress.IsIndeterminate = false;
        ModelDownloadProgress.Value = 0;
        ModelDownloadProgressText.Text = "Connecting to Hugging Face…";
        ModelStatusText.Text = "Downloading TranslateGemma…";
        ModelStatusIndicator.Fill = (Brush)FindResource("AccentBrush");
        StatusText.Text = "Downloading the local translation model in the background…";

        var progress = new Progress<ModelDownloadProgress>(UpdateModelDownloadProgress);
        try
        {
            await _modelDownloader.DownloadAsync(
                _analysis.Translation.ModelPath,
                progress,
                _modelDownloadCancellation.Token);
            if (_reallyClosing)
                return;

            ModelDownloadProgress.Value = 100;
            ModelDownloadProgressText.Text = "Download complete — verifying and loading…";
            StatusText.Text = "TranslateGemma downloaded. Loading the model…";
            await InitializeTranslationModelAsync();
        }
        catch (OperationCanceledException) when (!_lifetimeCancellation.IsCancellationRequested)
        {
            ModelStatusText.Text = "Model download paused. Translation remains unavailable.";
            ModelStatusIndicator.Fill = (Brush)FindResource("DangerBrush");
            ModelDownloadProgressText.Text = "Partial download kept for automatic resume.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ModelStatusText.Text = $"Model download failed: {exception.Message}";
            ModelStatusIndicator.Fill = (Brush)FindResource("DangerBrush");
            ModelDownloadProgressText.Text = "The partial download was kept when possible.";
            StatusText.Text = "Translation is unavailable. OCR and furigana still work.";
        }
        finally
        {
            _modelDownloadInProgress = false;
            _modelDownloadCancellation?.Dispose();
            _modelDownloadCancellation = null;
            if (!_reallyClosing && !_analysis.Translation.ModelAvailable)
            {
                ModelDownloadPanel.Visibility = Visibility.Visible;
                ModelDownloadButton.Content = File.Exists(
                    _analysis.Translation.ModelPath + ".download")
                    ? "Resume download"
                    : "Retry download";
                ModelDownloadButton.IsEnabled = true;
            }
        }
    }

    private void UpdateModelDownloadProgress(ModelDownloadProgress progress)
    {
        ModelDownloadProgress.IsIndeterminate = progress.TotalBytes <= 0;
        ModelDownloadProgress.Value = progress.Percentage;
        if (progress.IsVerifying)
        {
            ModelDownloadProgressText.Text = "Verifying SHA-256 integrity…";
            return;
        }
        var total = progress.TotalBytes > 0
            ? $" / {ModelDownloadService.FormatBytes(progress.TotalBytes)}"
            : "";
        var speed = progress.BytesPerSecond > 0
            ? $" • {ModelDownloadService.FormatBytes(progress.BytesPerSecond)}/s"
            : "";
        ModelDownloadProgressText.Text =
            $"{ModelDownloadService.FormatBytes(progress.BytesReceived)}{total}{speed}";
    }

    private void ShowModelLoading()
    {
        ModelDownloadPanel.Visibility = Visibility.Collapsed;
        ModelStatusText.Text = "Loading TranslateGemma model…";
        ModelStatusIndicator.Fill = (Brush)FindResource("AccentBrush");
    }

    private void ShowModelError(string message)
    {
        ModelStatusText.Text = $"Model loading failed: {message}";
        ModelStatusIndicator.Fill = (Brush)FindResource("DangerBrush");
        StatusText.Text = "Translation model failed to load. OCR remains available.";
    }

    private void UpdateHint()
    {
        var name = (HotkeyBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "the capture key";
        HintText.Text = $"Hold {name}, move the pointer to draw the area, then release the key. "
                        + "Press Escape or right-click to cancel.";
    }

    private void HideClicked(object sender, RoutedEventArgs e)
    {
        ShowInTaskbar = true;
        WindowState = WindowState.Minimized;
    }

    private void QuitClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownStarted)
            return;

        _shutdownStarted = true;
        _reallyClosing = true;
        if (!_testMode)
        {
            CollectConfiguration();
            _configService.Save(_config);
        }
        _analysisCancellation?.Cancel();
        _modelDownloadCancellation?.Cancel();
        _lifetimeCancellation.Cancel();
        _capture.Dispose();
        _analysis.Dispose();
        _supabaseVocabulary.Dispose();
        _supabaseAuth.Dispose();
        _lifetimeCancellation.Dispose();
        _resultWindow.ClosePermanently();
        Application.Current.Shutdown();
    }
}
