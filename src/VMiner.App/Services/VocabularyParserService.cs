using System.IO;
using NMeCab.Specialized;
using VMiner.Models;

namespace VMiner.Services;

public sealed class VocabularyParserService : IDisposable
{
    private readonly object _lock = new();
    private MeCabIpaDicTagger? _tagger;

    public IReadOnlyList<VocabularyToken> Parse(string text)
    {
        lock (_lock)
        {
            _tagger ??= MeCabIpaDicTagger.Create(
                Path.Combine(AppContext.BaseDirectory, "IpaDic"), []);

            return _tagger.Parse(text)
                .Where(node => !string.IsNullOrEmpty(node.Surface))
                .Select(node =>
                {
                    var surface = node.Surface;
                    var dictionaryForm = string.IsNullOrWhiteSpace(node.OriginalForm) ||
                                         node.OriginalForm == "*"
                        ? surface
                        : node.OriginalForm;
                    var reading = string.IsNullOrWhiteSpace(node.Reading) || node.Reading == "*"
                        ? surface
                        : KatakanaToHiragana(node.Reading);
                    var isVocabulary = node.PartsOfSpeech != "記号" &&
                                       surface.Any(IsJapaneseLetter);
                    return new VocabularyToken(
                        surface, dictionaryForm, reading, node.PartsOfSpeech, isVocabulary);
                })
                .ToArray();
        }
    }

    private static bool IsJapaneseLetter(char value) =>
        value is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff';

    private static string KatakanaToHiragana(string value) => string.Concat(value.Select(character =>
        character is >= '\u30a1' and <= '\u30f6'
            ? (char)(character - 0x60)
            : character));

    public void Dispose()
    {
        lock (_lock)
        {
            _tagger?.Dispose();
            _tagger = null;
        }
    }
}
