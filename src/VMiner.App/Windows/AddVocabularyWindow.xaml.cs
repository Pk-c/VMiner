using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using VMiner.Models;
using VMiner.Services;

namespace VMiner.Windows;

public partial class AddVocabularyWindow : Window
{
    private readonly VocabularyToken _token;
    private readonly TranslationService _translation;
    private readonly VocabularyStore _store;
    private bool _closing;

    public AddVocabularyWindow(
        VocabularyToken token,
        string japaneseSentence,
        string englishSentence,
        TranslationService translation,
        VocabularyStore store)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.EnableDarkTitleBar(
            new WindowInteropHelper(this).Handle);
        _token = token;
        _translation = translation;
        _store = store;
        WordBox.Text = token.DictionaryForm;
        ReadingBox.Text = token.Reading;
        JapaneseSentenceBox.Text = japaneseSentence;
        EnglishSentenceBox.Text = englishSentence;
        Loaded += WindowLoaded;
        Closing += WindowClosing;
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var existing = await _store.FindAsync(_token.DictionaryForm, _token.Reading);
            if (_closing)
                return;
            if (existing is not null)
            {
                DefinitionBox.Text = existing.Definition;
                DefinitionStatus.Text = "Already in your collection — this sentence will be added as an example.";
                AddButton.Content = "Add example";
                return;
            }

            if (!string.IsNullOrWhiteSpace(_token.Definition))
            {
                DefinitionBox.Text = _token.Definition;
                DefinitionStatus.Text = "Definition mined locally. You can edit it before saving.";
                return;
            }

            if (!_translation.ModelAvailable)
            {
                DefinitionStatus.Text = "Enter a definition manually; TranslateGemma is unavailable.";
                return;
            }

            DefinitionStatus.Text = "Mining definition…";
            var definition = await _translation.DefineWordAsync(
                _token.DictionaryForm, _token.Reading, JapaneseSentenceBox.Text);
            if (_closing)
                return;
            DefinitionBox.Text = definition;
            DefinitionStatus.Text = "Definition generated locally. You can edit it before saving.";
        }
        catch (Exception exception)
        {
            if (!_closing)
                DefinitionStatus.Text = $"Definition could not be generated: {exception.Message}";
        }
    }

    private async void AddClicked(object sender, RoutedEventArgs e)
    {
        var word = WordBox.Text.Trim();
        var reading = ReadingBox.Text.Trim();
        var definition = DefinitionBox.Text.Trim();
        if (word.Length == 0 || reading.Length == 0 || definition.Length == 0)
        {
            SaveStatus.Text = "Word, reading, and definition are required.";
            return;
        }

        AddButton.IsEnabled = false;
        SaveStatus.Text = "Saving…";
        try
        {
            await _store.AddOrUpdateAsync(
                word, reading, definition,
                JapaneseSentenceBox.Text.Trim(), EnglishSentenceBox.Text.Trim());
            DialogResult = true;
        }
        catch (Exception exception)
        {
            SaveStatus.Text = exception.Message;
            AddButton.IsEnabled = true;
        }
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => Close();

    private void WindowClosing(object? sender, CancelEventArgs e) => _closing = true;
}
