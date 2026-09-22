using VMiner.Models;

namespace VMiner.Services;

public sealed class VocabularyStore
{
    private readonly IVocabularyDatabaseBackend _backend;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal VocabularyStore(IVocabularyDatabaseBackend backend)
    {
        _backend = backend;
    }

    public bool IsConfigured => _backend.IsConnected;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _backend.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VocabularyEntry?> FindAsync(
        string word,
        string reading,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var database = await _backend.LoadAsync(cancellationToken).ConfigureAwait(false);
            var entry = database.Entries.FirstOrDefault(item =>
                string.Equals(item.Word, word, StringComparison.Ordinal) &&
                string.Equals(item.Reading, reading, StringComparison.Ordinal));
            return entry is null ? null : CloneEntry(entry);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<VocabularyEntry>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return [];

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var database = await _backend.LoadAsync(cancellationToken).ConfigureAwait(false);
            return database.Entries
                .OrderBy(entry => entry.Word, StringComparer.Ordinal)
                .Select(CloneEntry)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(
        string originalWord,
        string originalReading,
        VocabularyEntry updatedEntry,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var database = await _backend.LoadAsync(cancellationToken).ConfigureAwait(false);
            var entry = database.Entries.FirstOrDefault(item =>
                string.Equals(item.Word, originalWord, StringComparison.Ordinal) &&
                string.Equals(item.Reading, originalReading, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The vocabulary entry no longer exists.");
            var duplicate = database.Entries.Any(item => !ReferenceEquals(item, entry) &&
                string.Equals(item.Word, updatedEntry.Word, StringComparison.Ordinal) &&
                string.Equals(item.Reading, updatedEntry.Reading, StringComparison.Ordinal));
            if (duplicate)
                throw new InvalidOperationException(
                    "Another entry already uses this word and reading.");

            entry.Word = updatedEntry.Word;
            entry.Reading = updatedEntry.Reading;
            entry.Definition = updatedEntry.Definition;
            entry.Examples = updatedEntry.Examples
                .Where(example => !string.IsNullOrWhiteSpace(example.Japanese) ||
                                  !string.IsNullOrWhiteSpace(example.English))
                .Select(example => new SentencePair
                {
                    Japanese = example.Japanese.Trim(),
                    English = example.English.Trim(),
                })
                .DistinctBy(example => (example.Japanese, example.English))
                .ToList();
            await _backend.SaveAsync(database, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(
        string word,
        string reading,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var database = await _backend.LoadAsync(cancellationToken).ConfigureAwait(false);
            database.Entries.RemoveAll(entry =>
                string.Equals(entry.Word, word, StringComparison.Ordinal) &&
                string.Equals(entry.Reading, reading, StringComparison.Ordinal));
            await _backend.SaveAsync(database, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddOrUpdateAsync(
        string word,
        string reading,
        string definition,
        string japaneseSentence,
        string englishSentence,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var database = await _backend.LoadAsync(cancellationToken).ConfigureAwait(false);
            var entry = database.Entries.FirstOrDefault(item =>
                string.Equals(item.Word, word, StringComparison.Ordinal) &&
                string.Equals(item.Reading, reading, StringComparison.Ordinal));
            if (entry is null)
            {
                entry = new VocabularyEntry { Word = word, Reading = reading };
                database.Entries.Add(entry);
            }

            entry.Definition = definition;
            if (!string.IsNullOrWhiteSpace(japaneseSentence) &&
                !entry.ContainsExample(japaneseSentence, englishSentence))
            {
                entry.Examples.Add(new SentencePair
                {
                    Japanese = japaneseSentence,
                    English = englishSentence,
                });
            }

            await _backend.SaveAsync(database, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureConnected()
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Sign in to your VMiner account before adding vocabulary.");
    }

    private static VocabularyEntry CloneEntry(VocabularyEntry entry) => new()
    {
        Word = entry.Word,
        Reading = entry.Reading,
        Definition = entry.Definition,
        Examples = entry.Examples.Select(example => new SentencePair
        {
            Japanese = example.Japanese,
            English = example.English,
        }).ToList(),
    };
}
