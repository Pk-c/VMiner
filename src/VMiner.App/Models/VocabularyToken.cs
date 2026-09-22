namespace VMiner.Models;

public sealed record VocabularyToken(
    string Surface,
    string DictionaryForm,
    string Reading,
    string PartOfSpeech,
    bool IsVocabulary,
    string Definition = "",
    bool IsInCollection = false)
{
    public string DisplayReading => ContainsKanji(Surface) &&
                                    !string.Equals(Surface, Reading, StringComparison.Ordinal)
        ? Reading
        : "";

    public string TooltipText => string.IsNullOrWhiteSpace(Definition)
        ? "Definition unavailable"
        : Definition;

    private static bool ContainsKanji(string value) =>
        value.Any(character => character is >= '\u3400' and <= '\u9fff');
}
