using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VMiner.Models;
using VMiner.Services;

namespace VMiner.Windows;

public partial class MainWindow : Window
{
    private readonly ConfigService _configService = new();
    private readonly AppConfig _config;
    private readonly AnalysisService _analysis;
    private readonly VocabularyStore _vocabularyStore;
    private readonly GlobalCaptureService _capture;
    private readonly ResultWindow _resultWindow;
    private CancellationTokenSource? _analysisCancellation;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private IReadOnlyList<VocabularyEntry> _collectionEntries = [];
    private int _requestId;
    private bool _reallyClosing;
    private bool _shutdownStarted;

    public MainWindow() : this(false)
    {
    }

    internal MainWindow(bool testMode)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.EnableDarkTitleBar(
            new WindowInteropHelper(this).Handle);
        _config = _configService.Load();
        _analysis = new AnalysisService(_config);
        _vocabularyStore = new VocabularyStore(_config.VocabularyDatabasePath);
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
        await EnsureVocabularyDatabaseAsync(promptIfMissing: true);
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

    private async void ChooseVocabularyDatabaseClicked(object sender, RoutedEventArgs e) =>
        await EnsureVocabularyDatabaseAsync(promptIfMissing: true, forcePrompt: true);

    private async Task EnsureVocabularyDatabaseAsync(
        bool promptIfMissing,
        bool forcePrompt = false)
    {
        var selectedPath = _config.VocabularyDatabasePath;
        if (forcePrompt || (promptIfMissing && string.IsNullOrWhiteSpace(selectedPath)))
        {
            var dialog = new SaveFileDialog
            {
                Title = "Choose your VMiner vocabulary database",
                Filter = "VMiner vocabulary database (*.json)|*.json|JSON files (*.json)|*.json",
                DefaultExt = ".json",
                AddExtension = true,
                FileName = string.IsNullOrWhiteSpace(selectedPath)
                    ? "vminer-vocabulary.json"
                    : Path.GetFileName(selectedPath),
                OverwritePrompt = false,
            };
            if (!string.IsNullOrWhiteSpace(selectedPath))
                dialog.InitialDirectory = Path.GetDirectoryName(selectedPath);
            if (dialog.ShowDialog(this) != true)
            {
                UpdateVocabularyStatus();
                return;
            }
            selectedPath = dialog.FileName;
        }

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            UpdateVocabularyStatus();
            return;
        }

        var previousPath = _vocabularyStore.DatabasePath;
        try
        {
            _vocabularyStore.SetDatabasePath(selectedPath);
            VocabularyStatusText.Text = "Preparing vocabulary database…";
            await _vocabularyStore.InitializeAsync();
            _config.VocabularyDatabasePath = _vocabularyStore.DatabasePath;
            _configService.Save(_config);
            UpdateVocabularyStatus();
            if (MainTabs.SelectedItem == CollectionTab)
                await RefreshCollectionAsync();
        }
        catch (Exception exception)
        {
            _vocabularyStore.SetDatabasePath(previousPath);
            VocabularyStatusText.Text = "Vocabulary database could not be opened";
            MessageBox.Show(this, exception.Message, "VMiner", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void UpdateVocabularyStatus()
    {
        if (string.IsNullOrWhiteSpace(_config.VocabularyDatabasePath))
        {
            VocabularyStatusText.Text = "No database selected";
            VocabularyStatusText.ToolTip = null;
            return;
        }

        VocabularyStatusText.Text = _config.VocabularyDatabasePath;
        VocabularyStatusText.ToolTip = _config.VocabularyDatabasePath;
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
            CollectionStatusText.Text = "Choose a vocabulary database in Capture & model.";
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
            ModelStatusText.Text = "TranslateGemma model not found in models";
            ModelStatusIndicator.Fill = (Brush)FindResource("DangerBrush");
            return;
        }

        if (!_analysis.Translation.IsLoaded)
        {
            ShowModelLoading();
            return;
        }

        ModelStatusText.Text = "TranslateGemma ready • Japanese → English";
        ModelStatusIndicator.Fill = (Brush)FindResource("SuccessBrush");
    }

    private void ShowModelLoading()
    {
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
        CollectConfiguration();
        _configService.Save(_config);
        _analysisCancellation?.Cancel();
        _lifetimeCancellation.Cancel();
        _capture.Dispose();
        _analysis.Dispose();
        _lifetimeCancellation.Dispose();
        _resultWindow.ClosePermanently();
        Application.Current.Shutdown();
    }
}
