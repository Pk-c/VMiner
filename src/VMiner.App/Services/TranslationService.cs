using System.Text;
using System.IO;
using LLama;
using LLama.Common;
using LLama.Sampling;
using VMiner.Models;

namespace VMiner.Services;

public sealed class TranslationService : IDisposable
{
    private readonly AppConfig _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _initializationLock = new();
    private Task? _initializationTask;
    private LLamaWeights? _weights;
    private ModelParams? _parameters;
    private StatelessExecutor? _executor;
    private bool _disposed;

    public TranslationService(AppConfig config)
    {
        _config = config;
    }

    public string ModelPath => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, _config.TranslationModel));

    public bool ModelAvailable => File.Exists(ModelPath);

    public bool IsLoaded
    {
        get
        {
            lock (_initializationLock)
                return _executor is not null;
        }
    }

    public bool IsLoading
    {
        get
        {
            lock (_initializationLock)
                return _initializationTask is { IsCompleted: false };
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!ModelAvailable)
            return Task.CompletedTask;

        Task initialization;
        lock (_initializationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            initialization = _initializationTask ??= Task.Run(LoadModel);
        }

        return cancellationToken.CanBeCanceled
            ? initialization.WaitAsync(cancellationToken)
            : initialization;
    }

    public async Task<string> TranslateAsync(string japanese, CancellationToken cancellationToken)
    {
        if (!ModelAvailable)
            return "(TranslateGemma model missing — place the GGUF file in the models folder)";

        return await GenerateAsync(BuildTranslationPrompt(japanese), 384, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string> DefineWordAsync(
        string word,
        string reading,
        string sentence,
        CancellationToken cancellationToken = default)
    {
        if (!ModelAvailable)
            return "";

        return await GenerateAsync(
                BuildDefinitionPrompt(word, reading, sentence), 96, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> DefineWordsAsync(
        IReadOnlyList<VocabularyToken> tokens,
        string sentence,
        CancellationToken cancellationToken = default)
    {
        if (!ModelAvailable || tokens.Count == 0)
            return Enumerable.Repeat("", tokens.Count).ToArray();

        var prompt = BuildDefinitionsPrompt(tokens, sentence);
        var response = await GenerateAsync(
                prompt, Math.Min(512, 48 + tokens.Count * 32), cancellationToken)
            .ConfigureAwait(false);
        var definitions = new string[tokens.Count];
        foreach (var line in response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var cleaned = line.Trim().TrimStart('-', '*').Trim();
            var separator = cleaned.IndexOf('\t');
            if (separator < 0)
                separator = cleaned.IndexOf(':');
            if (separator <= 0 || !int.TryParse(cleaned[..separator].Trim(), out var index) ||
                index < 0 || index >= definitions.Length)
                continue;
            var value = cleaned[(separator + 1)..]
                .Replace("<TAB>", "\t", StringComparison.OrdinalIgnoreCase);
            definitions[index] = value.Split('\t', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()?.Trim().Trim('"') ?? "";
        }
        return definitions;
    }

    private async Task<string> GenerateAsync(
        string prompt,
        int maximumTokens,
        CancellationToken cancellationToken)
    {

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => InferAsync(prompt, maximumTokens, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void LoadModel()
    {
        var parameters = new ModelParams(ModelPath)
        {
            ContextSize = 2048,
            GpuLayerCount = 999,
            Threads = Math.Max(2, Environment.ProcessorCount - 2),
        };
        var weights = LLamaWeights.LoadFromFile(parameters);
        var executor = new StatelessExecutor(weights, parameters)
        {
            ApplyTemplate = false,
        };

        lock (_initializationLock)
        {
            if (_disposed)
            {
                weights.Dispose();
                throw new ObjectDisposedException(nameof(TranslationService));
            }

            _parameters = parameters;
            _weights = weights;
            _executor = executor;
        }
    }

    private async Task<string> InferAsync(
        string prompt,
        int maximumTokens,
        CancellationToken cancellationToken)
    {
        var inference = new InferenceParams
        {
            MaxTokens = maximumTokens,
            AntiPrompts = ["<end_of_turn>"],
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0,
            },
        };

        var result = new StringBuilder();
        await foreach (var token in _executor!.InferAsync(
                           prompt, inference, cancellationToken).ConfigureAwait(false))
            result.Append(token);

        return result.ToString().Replace("<end_of_turn>", "").Trim();
    }

    private static string BuildTranslationPrompt(string current)
    {
        return "<start_of_turn>user\n"
             + "You are a professional Japanese (ja) to English (en) translator. "
             + "Your goal is to accurately convey the meaning and nuances of the original Japanese "
             + "text while adhering to English grammar, vocabulary, and cultural sensitivities.\n"
             + "Produce only the English translation, without any additional explanations or "
             + "commentary. Please translate the following Japanese text into English:\n\n\n"
             + current.Trim()
             + "<end_of_turn>\n<start_of_turn>model\n";
    }

    private static string BuildDefinitionPrompt(string word, string reading, string sentence)
    {
        return "<start_of_turn>user\n"
             + "You are a concise Japanese-English dictionary. Give only a short English "
             + "dictionary definition for the Japanese word below. Do not repeat the word, "
             + "do not add notes, and do not use a complete sentence.\n\n"
             + $"Word: {word}\nReading: {reading}\nContext: {sentence.Trim()}"
             + "<end_of_turn>\n<start_of_turn>model\n";
    }

    private static string BuildDefinitionsPrompt(
        IReadOnlyList<VocabularyToken> tokens,
        string sentence)
    {
        var words = string.Join('\n', tokens.Select((token, index) =>
            $"{index}\t{token.DictionaryForm}\t{token.Reading}"));
        return "<start_of_turn>user\n"
             + "You are a concise Japanese-English dictionary. Define every numbered Japanese "
             + "word using its meaning in the supplied sentence. Return exactly one TSV line per "
             + "word in the format: number<TAB>short English definition. Do not repeat Japanese, "
             + "do not add headings, notes, bullets, or examples.\n\n"
             + $"Sentence: {sentence.Trim()}\n\nWords:\n{words}"
             + "<end_of_turn>\n<start_of_turn>model\n";
    }

    public void Dispose()
    {
        LLamaWeights? weights;
        lock (_initializationLock)
        {
            _disposed = true;
            weights = _weights;
            _weights = null;
            _executor = null;
        }
        weights?.Dispose();
    }
}
