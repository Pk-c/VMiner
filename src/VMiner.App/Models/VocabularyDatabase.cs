namespace VMiner.Models;

public sealed class VocabularyDatabase
{
    public int Version { get; set; } = 1;
    public List<VocabularyEntry> Entries { get; set; } = [];
}

public sealed class VocabularyEntry
{
    public Guid Id { get; set; }
    public string Word { get; set; } = "";
    public string Reading { get; set; } = "";
    public string Definition { get; set; } = "";
    public List<SentencePair> Examples { get; set; } = [];

    public bool ContainsExample(string japanese, string english) => Examples.Any(example =>
        string.Equals(example.Japanese, japanese.Trim(), StringComparison.Ordinal) &&
        string.Equals(example.English, english.Trim(), StringComparison.Ordinal));
}

public sealed class SentencePair
{
    public Guid Id { get; set; }
    public string Japanese { get; set; } = "";
    public string English { get; set; } = "";
}
