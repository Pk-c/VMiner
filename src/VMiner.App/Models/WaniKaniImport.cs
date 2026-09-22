namespace VMiner.Models;

public sealed record WaniKaniVocabularyItem(
    long SubjectId,
    DateTimeOffset UpdatedAt,
    string Word,
    string Reading,
    string Definition,
    IReadOnlyList<SentencePair> Examples);

public sealed record WaniKaniFetchResult(
    string Username,
    IReadOnlyList<WaniKaniVocabularyItem> Entries);

public sealed record VocabularyImportResult(
    int NewEntries,
    int ExistingEntries,
    int NewExamples)
{
    public static VocabularyImportResult operator +(
        VocabularyImportResult left,
        VocabularyImportResult right) => new(
            left.NewEntries + right.NewEntries,
            left.ExistingEntries + right.ExistingEntries,
            left.NewExamples + right.NewExamples);
}

public sealed record WaniKaniImportProgress(
    string Message,
    int Completed,
    int? Total = null);
