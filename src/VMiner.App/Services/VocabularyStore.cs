using VMiner.Models;

namespace VMiner.Services;

public sealed class VocabularyStore
{
    private readonly IVocabularyDatabaseBackend _backend;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VocabularyDatabase _database = new();

    internal VocabularyStore(IVocabularyDatabaseBackend backend)
    {
        _backend = backend;
    }

    public bool IsConfigured => _backend.IsConnected;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        ReloadAsync(cancellationToken);

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _database = await _backend.LoadAsync(cancellationToken).ConfigureAwait(false);
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
            var entry = FindEntry(word, reading);
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
            return _database.Entries
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
            var entry = FindEntry(originalWord, originalReading)
                ?? throw new InvalidOperationException("The vocabulary entry no longer exists.");
            var duplicate = _database.Entries.Any(item => item.Id != entry.Id &&
                string.Equals(item.Word, updatedEntry.Word, StringComparison.Ordinal) &&
                string.Equals(item.Reading, updatedEntry.Reading, StringComparison.Ordinal));
            if (duplicate)
                throw new InvalidOperationException(
                    "Another entry already uses this word and reading.");

            var replacement = new VocabularyEntry
            {
                Id = entry.Id,
                Word = updatedEntry.Word.Trim(),
                Reading = updatedEntry.Reading.Trim(),
                Definition = updatedEntry.Definition.Trim(),
                Examples = updatedEntry.Examples
                    .Where(example => !string.IsNullOrWhiteSpace(example.Japanese) ||
                                      !string.IsNullOrWhiteSpace(example.English))
                    .Select(example => new SentencePair
                    {
                        Id = example.Id,
                        Japanese = example.Japanese.Trim(),
                        English = example.English.Trim(),
                    })
                    .DistinctBy(example => (example.Japanese, example.English))
                    .ToList(),
            };
            await _backend.ReplaceAsync(entry.Id, replacement, cancellationToken)
                .ConfigureAwait(false);
            var index = _database.Entries.IndexOf(entry);
            _database.Entries[index] = CloneEntry(replacement);
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
            var entry = FindEntry(word, reading);
            if (entry is null)
                return;
            await _backend.DeleteAsync(entry.Id, cancellationToken).ConfigureAwait(false);
            _database.Entries.Remove(entry);
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
        word = word.Trim();
        reading = reading.Trim();
        definition = definition.Trim();
        japaneseSentence = japaneseSentence.Trim();
        englishSentence = englishSentence.Trim();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entryId = await _backend.AddOrUpdateAsync(
                word, reading, definition, japaneseSentence, englishSentence, cancellationToken)
                .ConfigureAwait(false);
            var entry = FindEntry(word, reading);
            if (entry is null)
            {
                entry = new VocabularyEntry { Id = entryId, Word = word, Reading = reading };
                _database.Entries.Add(entry);
            }

            entry.Id = entryId;
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
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VocabularyImportResult> ImportWaniKaniAsync(
        IReadOnlyList<WaniKaniVocabularyItem> entries,
        IProgress<WaniKaniImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (entries.Count == 0)
            return new VocabularyImportResult(0, 0, 0);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _backend.ImportWaniKaniAsync(
                entries, progress, cancellationToken).ConfigureAwait(false);
            _database = await _backend.LoadAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private VocabularyEntry? FindEntry(string word, string reading) =>
        _database.Entries.FirstOrDefault(item =>
            string.Equals(item.Word, word, StringComparison.Ordinal) &&
            string.Equals(item.Reading, reading, StringComparison.Ordinal));

    private void EnsureConnected()
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Sign in to your VMiner account before adding vocabulary.");
    }

    private static VocabularyEntry CloneEntry(VocabularyEntry entry) => new()
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
    };
}
