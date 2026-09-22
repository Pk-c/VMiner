using System.Windows;
using System.Windows.Interop;
using VMiner.Models;
using VMiner.Services;

namespace VMiner.Windows;

public partial class AddExampleWindow : Window
{
    private readonly VocabularyEntry _entry;
    private readonly VocabularyStore _store;

    public AddExampleWindow(
        VocabularyEntry entry,
        string japaneseSentence,
        string englishSentence,
        VocabularyStore store)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.EnableDarkTitleBar(
            new WindowInteropHelper(this).Handle);
        _entry = entry;
        _store = store;
        DefinitionText.Text = entry.Definition;
        WordReadingText.Text = $"{entry.Word}  •  {entry.Reading}";
        JapaneseSentenceBox.Text = japaneseSentence;
        EnglishSentenceBox.Text = englishSentence;
    }

    private async void AddClicked(object sender, RoutedEventArgs e)
    {
        var japanese = JapaneseSentenceBox.Text.Trim();
        var english = EnglishSentenceBox.Text.Trim();
        if (japanese.Length == 0 || english.Length == 0)
        {
            StatusText.Text = "Both sentence fields are required.";
            return;
        }

        AddButton.IsEnabled = false;
        StatusText.Text = "Saving…";
        try
        {
            await _store.AddOrUpdateAsync(
                _entry.Word, _entry.Reading, _entry.Definition, japanese, english);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            AddButton.IsEnabled = true;
        }
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => Close();
}
