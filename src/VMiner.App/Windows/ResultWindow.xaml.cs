using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using VMiner.Models;
using VMiner.Services;

namespace VMiner.Windows;

public partial class ResultWindow : Window
{
    public event EventHandler? VocabularyChanged;

    private readonly AppConfig _config;
    private readonly TranslationService _translationService;
    private readonly VocabularyStore _vocabularyStore;
    private string _japanese = "";
    private string _reading = "";
    private string _translation = "";
    private IReadOnlyList<VocabularyToken> _displayTokens = [];
    private bool _reallyClosing;

    public ResultWindow(
        AppConfig config,
        TranslationService translationService,
        VocabularyStore vocabularyStore)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.EnableDarkTitleBar(
            new WindowInteropHelper(this).Handle);
        _config = config;
        _translationService = translationService;
        _vocabularyStore = vocabularyStore;
        PinCheck.IsChecked = config.AlwaysOnTop;
        Closing += (_, args) =>
        {
            if (_reallyClosing)
                return;
            args.Cancel = true;
            Hide();
        };
    }

    public void ShowPending(string message)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource("MutedBrush");
        SetLoadingMessage(message);
        LoadingOverlay.Visibility = Visibility.Visible;
        ShowWithoutActivation();
    }

    public void UpdatePending(string message)
    {
        StatusText.Text = message;
        SetLoadingMessage(message);
    }

    internal (int FrameCount, bool IsPlaying) TestMiningAnimation()
    {
        ShowPending("Reading Japanese text…");
        UpdateLayout();
        return (MiningAnimation.FrameCount, MiningAnimation.IsPlaying);
    }

    private void SetLoadingMessage(string message)
    {
        LoadingText.Text = message;
        MiningAnimation.Visibility = Visibility.Visible;
    }

    public void ShowError(string message)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource("DangerBrush");
        LoadingOverlay.Visibility = Visibility.Collapsed;
        ShowWithoutActivation();
    }

    public async Task ShowResultAsync(Analysis analysis, string subtitle)
    {
        _japanese = analysis.DetectedText;
        _reading = analysis.Reading;
        _translation = analysis.Translation;
        _displayTokens = await ApplyCollectionStateAsync(analysis.Tokens);
        VocabularyTokens.ItemsSource = _displayTokens;
        ReadingText.Text = analysis.Reading;
        ReadingText.FontSize = Math.Max(10, _config.FontSize - 9);
        TranslationText.Text = string.IsNullOrWhiteSpace(analysis.Translation)
            ? "(no Japanese text detected)"
            : analysis.Translation;
        TranslationText.FontSize = Math.Max(11, _config.FontSize - 8);
        StatusText.Text = subtitle;
        StatusText.Foreground = (Brush)FindResource("MutedBrush");
        LoadingOverlay.Visibility = Visibility.Collapsed;
        ShowWithoutActivation();
    }

    private void ShowWithoutActivation()
    {
        Topmost = PinCheck.IsChecked == true;
        if (!IsVisible)
            Show();
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.ConfigureOverlayWindow(handle, false, !_config.StealFocus);
        ActivateIfRequested();
    }

    private void ActivateIfRequested()
    {
        if (_config.StealFocus)
            Activate();
    }

    private void ApplyPin(object sender, RoutedEventArgs e)
    {
        Topmost = PinCheck.IsChecked == true;
        _config.AlwaysOnTop = Topmost;
    }

    private static void Copy(string value)
    {
        if (!string.IsNullOrEmpty(value))
            Clipboard.SetText(value);
    }

    private void CopyJapanese(object sender, RoutedEventArgs e) => Copy(_japanese);
    private void CopyReading(object sender, RoutedEventArgs e) => Copy(_reading);
    private void CopyTranslation(object sender, RoutedEventArgs e) => Copy(_translation);

    private async void VocabularyTokenClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VocabularyToken token })
            return;
        if (!_vocabularyStore.IsConfigured)
        {
            MessageBox.Show(this,
                "Sign in to your VMiner account from the main window first.",
                "VMiner", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var existing = await _vocabularyStore.FindAsync(
                token.DictionaryForm, token.Reading);
            if (existing?.ContainsExample(_japanese, _translation) == true)
            {
                const string message = "This sentence already exists in your collection.";
                StatusText.Text = message;
                StatusText.Foreground = (Brush)FindResource("AccentBrush");
                MessageBox.Show(this, message, "Sentence already saved",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Window dialog = existing is not null
                ? new AddExampleWindow(
                    existing, _japanese, _translation, _vocabularyStore)
                : new AddVocabularyWindow(
                    token, _japanese, _translation, _translationService, _vocabularyStore);
            dialog.Owner = this;
            if (dialog.ShowDialog() != true)
                return;

            StatusText.Text = existing is not null
                ? $"Example added for {existing.Definition}."
                : $"{token.Definition} saved to your collection.";
            StatusText.Foreground = (Brush)FindResource("SuccessBrush");
            _displayTokens = await ApplyCollectionStateAsync(_displayTokens);
            VocabularyTokens.ItemsSource = _displayTokens;
            VocabularyChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
        }
    }

    private async Task<IReadOnlyList<VocabularyToken>> ApplyCollectionStateAsync(
        IReadOnlyList<VocabularyToken> tokens)
    {
        if (!_vocabularyStore.IsConfigured)
            return tokens;

        try
        {
            var entries = await _vocabularyStore.GetAllAsync();
            var saved = entries.ToDictionary(
                entry => (entry.Word, entry.Reading),
                entry => entry,
                EqualityComparer<(string Word, string Reading)>.Default);
            return tokens.Select(token => saved.TryGetValue(
                    (token.DictionaryForm, token.Reading), out var entry)
                    ? token with
                    {
                        Definition = entry.Definition,
                        IsInCollection = true,
                    }
                    : token with { IsInCollection = false })
                .ToArray();
        }
        catch
        {
            return tokens;
        }
    }

    public void ClosePermanently()
    {
        _reallyClosing = true;
        Close();
    }
}
