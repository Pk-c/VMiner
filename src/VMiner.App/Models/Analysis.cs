namespace VMiner.Models;

public sealed record RubySegment(string Text, string Reading = "");

public sealed record Analysis(
    string DetectedText,
    IReadOnlyList<RubySegment> Segments,
    string Reading,
    string Translation,
    IReadOnlyList<VocabularyToken> Tokens)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(DetectedText);
}
