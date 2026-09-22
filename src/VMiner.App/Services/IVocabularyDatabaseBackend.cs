using VMiner.Models;

namespace VMiner.Services;

internal interface IVocabularyDatabaseBackend
{
    bool IsConnected { get; }
    Task<VocabularyDatabase> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(
        VocabularyDatabase database,
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

    public Task SaveAsync(
        VocabularyDatabase database,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _database = Clone(database);
        return Task.CompletedTask;
    }

    private static VocabularyDatabase Clone(VocabularyDatabase source) => new()
    {
        Version = source.Version,
        Entries = source.Entries.Select(entry => new VocabularyEntry
        {
            Word = entry.Word,
            Reading = entry.Reading,
            Definition = entry.Definition,
            Examples = entry.Examples.Select(example => new SentencePair
            {
                Japanese = example.Japanese,
                English = example.English,
            }).ToList(),
        }).ToList(),
    };
}
