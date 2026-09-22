using VMiner.Models;

namespace VMiner.Services;

internal interface IVocabularyDatabaseBackend
{
    bool IsConnected { get; }
    Task<VocabularyDatabase> LoadAsync(CancellationToken cancellationToken = default);
    Task<Guid> AddOrUpdateAsync(
        string word,
        string reading,
        string definition,
        string japaneseSentence,
        string englishSentence,
        CancellationToken cancellationToken = default);
    Task ReplaceAsync(
        Guid entryId,
        VocabularyEntry entry,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(
        Guid entryId,
        CancellationToken cancellationToken = default);
    Task<VocabularyImportResult> ImportWaniKaniAsync(
        IReadOnlyList<WaniKaniVocabularyItem> entries,
        IProgress<WaniKaniImportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

internal sealed class MemoryVocabularyDatabaseBackend : IVocabularyDatabaseBackend
{
    private VocabularyDatabase _database = new();

    public bool IsConnected => true;

    public Task<VocabularyDatabase> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Clone(_database));
    }

    public Task<Guid> AddOrUpdateAsync(
        string word,
        string reading,
        string definition,
        string japaneseSentence,
        string englishSentence,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = _database.Entries.FirstOrDefault(item =>
            item.Word == word && item.Reading == reading);
        if (entry is null)
        {
            entry = new VocabularyEntry { Id = Guid.NewGuid(), Word = word, Reading = reading };
            _database.Entries.Add(entry);
        }

        entry.Definition = definition;
        if (!string.IsNullOrWhiteSpace(japaneseSentence) &&
            !entry.ContainsExample(japaneseSentence, englishSentence))
        {
            entry.Examples.Add(new SentencePair
            {
                Id = Guid.NewGuid(),
                Japanese = japaneseSentence,
                English = englishSentence,
            });
        }

        return Task.FromResult(entry.Id);
    }

    public Task ReplaceAsync(
        Guid entryId,
        VocabularyEntry entry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = _database.Entries.FindIndex(item => item.Id == entryId);
        if (index < 0)
            throw new InvalidOperationException("The vocabulary entry no longer exists.");
        var replacement = CloneEntry(entry);
        replacement.Id = entryId;
        foreach (var example in replacement.Examples.Where(item => item.Id == Guid.Empty))
            example.Id = Guid.NewGuid();
        _database.Entries[index] = replacement;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _database.Entries.RemoveAll(item => item.Id == entryId);
        return Task.CompletedTask;
    }

    public Task<VocabularyImportResult> ImportWaniKaniAsync(
        IReadOnlyList<WaniKaniVocabularyItem> entries,
        IProgress<WaniKaniImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new VocabularyImportResult(0, 0, 0);
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var imported = entries[index];
            var existing = _database.Entries.FirstOrDefault(item =>
                item.Word == imported.Word && item.Reading == imported.Reading);
            if (existing is null)
            {
                existing = new VocabularyEntry
                {
                    Id = Guid.NewGuid(),
                    Word = imported.Word,
                    Reading = imported.Reading,
                    Definition = imported.Definition,
                };
                _database.Entries.Add(existing);
                result += new VocabularyImportResult(1, 0, 0);
            }
            else
            {
                result += new VocabularyImportResult(0, 1, 0);
            }

            var newExamples = 0;
            foreach (var example in imported.Examples)
            {
                if (existing.ContainsExample(example.Japanese, example.English))
                    continue;
                existing.Examples.Add(new SentencePair
                {
                    Id = Guid.NewGuid(),
                    Japanese = example.Japanese,
                    English = example.English,
                });
                newExamples++;
            }
            result += new VocabularyImportResult(0, 0, newExamples);
            progress?.Report(new WaniKaniImportProgress(
                $"Importing WaniKani vocabulary… {index + 1:N0}/{entries.Count:N0}",
                index + 1, entries.Count));
        }
        return Task.FromResult(result);
    }

    private static VocabularyDatabase Clone(VocabularyDatabase source) => new()
    {
        Version = source.Version,
        Entries = source.Entries.Select(entry => new VocabularyEntry
        {
            Id = entry.Id,
            Word = entry.Word,
            Reading = entry.Reading,
            Definition = entry.Definition,
            Examples = entry.Examples.Select(example => new SentencePair
            {
                Id = example.Id,
                Japanese = example.Japanese,
                English = example.English,
            }).ToList(),
        }).ToList(),
    };

    private static VocabularyEntry CloneEntry(VocabularyEntry entry) => Clone(new VocabularyDatabase
    {
        Entries = [entry],
    }).Entries[0];
}
