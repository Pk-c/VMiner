using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using VMiner.Models;
using VMiner.Services;

namespace VMiner.Windows;

public partial class EditVocabularyWindow : Window
{
    private readonly string _originalWord;
    private readonly string _originalReading;
    private readonly VocabularyStore _store;
    private readonly ObservableCollection<SentencePair> _examples;

    public EditVocabularyWindow(VocabularyEntry entry, VocabularyStore store)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.EnableDarkTitleBar(
            new WindowInteropHelper(this).Handle);
        _originalWord = entry.Word;
        _originalReading = entry.Reading;
        _store = store;
        WordBox.Text = entry.Word;
        ReadingBox.Text = entry.Reading;
        DefinitionBox.Text = entry.Definition;
        _examples = new ObservableCollection<SentencePair>(entry.Examples.Select(example => new SentencePair
        {
            Id = example.Id,
            Japanese = example.Japanese,
            English = example.English,
        }));
        ExamplesList.ItemsSource = _examples;
    }

    private void AddExampleClicked(object sender, RoutedEventArgs e) =>
        _examples.Add(new SentencePair());

    private void RemoveExampleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SentencePair example })
            _examples.Remove(example);
    }

    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        var word = WordBox.Text.Trim();
        var reading = ReadingBox.Text.Trim();
        var definition = DefinitionBox.Text.Trim();
        if (word.Length == 0 || reading.Length == 0 || definition.Length == 0)
        {
            StatusText.Text = "Word, reading, and definition are required.";
            return;
        }

        SaveButton.IsEnabled = false;
        StatusText.Text = "Saving…";
        try
        {
            await _store.UpdateAsync(_originalWord, _originalReading, new VocabularyEntry
            {
                Word = word,
                Reading = reading,
                Definition = definition,
                Examples = _examples.ToList(),
            });
            DialogResult = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            SaveButton.IsEnabled = true;
        }
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => Close();
}
