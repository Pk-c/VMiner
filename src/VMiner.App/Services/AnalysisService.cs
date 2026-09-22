using System.Windows.Media.Imaging;
using VMiner.Models;

namespace VMiner.Services;

public sealed class AnalysisService : IDisposable
{
    private readonly OcrService _ocr = new();
    private readonly FuriganaService _furigana = new();
    private readonly VocabularyParserService _vocabularyParser = new();
    private readonly TranslationService _translation;

    public AnalysisService(AppConfig config)
    {
        _translation = new TranslationService(config);
    }

    public TranslationService Translation => _translation;

    public async Task<Analysis> AnalyseAsync(
        BitmapSource image,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("Reading Japanese text…");
        var text = await Task.Run(
            () => _ocr.ReadJapaneseAsync(image), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
            return new Analysis("", [], "", "", []);

        var furiganaTask = Task.Run(() => _furigana.ConvertAsync(text), cancellationToken);
        var tokensTask = Task.Run(() => _vocabularyParser.Parse(text), cancellationToken);

        if (_translation.ModelAvailable && !_translation.IsLoaded)
            progress?.Report("Waiting for the translation model…");
        await _translation.InitializeAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(_translation.ModelAvailable
            ? "Mining…"
            : "Preparing reading…");
        var translationTask = _translation.TranslateAsync(text, cancellationToken);
        await Task.WhenAll(furiganaTask, translationTask, tokensTask).ConfigureAwait(false);
        var (segments, reading) = await furiganaTask;
        var tokens = await tokensTask;
        if (_translation.ModelAvailable)
        {
            progress?.Report("Mining vocabulary…");
            var vocabulary = tokens.Where(token => token.IsVocabulary)
                .DistinctBy(token => (token.DictionaryForm, token.Reading))
                .ToArray();
            var definitions = await _translation.DefineWordsAsync(
                vocabulary, text, cancellationToken).ConfigureAwait(false);
            var definitionMap = vocabulary.Select((token, index) =>
                    (Key: (token.DictionaryForm, token.Reading), Definition: definitions[index]))
                .ToDictionary(item => item.Key, item => item.Definition);
            tokens = tokens.Select(token => definitionMap.TryGetValue(
                    (token.DictionaryForm, token.Reading), out var definition)
                    ? token with { Definition = definition }
                    : token)
                .ToArray();
        }
        return new Analysis(text, segments, reading, await translationTask, tokens);
    }

    public async Task<string> SelfCheckAsync()
    {
        var ocr = OcrService.CheckJapaneseAvailability();
        var sample = await _furigana.ConvertAsync("日本語です");
        var tokens = _vocabularyParser.Parse("今日はいい天気ですね。");
        var vocabulary = string.Join('/', tokens
            .Where(token => token.IsVocabulary)
            .Select(token => $"{token.DictionaryForm}:{token.Reading}"));
        var translation = "TranslateGemma unavailable";
        var definition = "definition unavailable";
        if (_translation.ModelAvailable)
        {
            translation = $"TranslateGemma OK (« {await _translation.TranslateAsync("今日はいい天気ですね。", CancellationToken.None)} »)";
            var vocabularyTokens = tokens.Where(token => token.IsVocabulary)
                .DistinctBy(token => (token.DictionaryForm, token.Reading))
                .ToArray();
            var definitions = await _translation.DefineWordsAsync(
                vocabularyTokens, "今日はいい天気ですね。", CancellationToken.None);
            if (definitions.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("Batch vocabulary definition test failed.");
            definition = $"definitions OK (« {string.Join(" / ", definitions)} »)";
        }
        return $"{ocr} | furigana OK ({sample.Reading}) | vocabulary OK ({vocabulary}) | {translation} | {definition}";
    }

    public void Dispose()
    {
        _furigana.Dispose();
        _vocabularyParser.Dispose();
        _translation.Dispose();
    }
}
