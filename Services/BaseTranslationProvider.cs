namespace genslation.Services;

using System.Diagnostics;
using System.Text.RegularExpressions;
using genslation.Interfaces;
using genslation.Models;
using Microsoft.Extensions.Logging;

public abstract class BaseTranslationProvider : ITranslationProvider
{
    protected readonly ILogger _logger;
    protected readonly TranslationOptions _defaultOptions;

    protected BaseTranslationProvider(ILogger logger, TranslationOptions defaultOptions)
    {
        _logger = logger;
        _defaultOptions = defaultOptions;
    }

    public abstract string Name { get; }

    public abstract Task<TranslationResult> TranslateChunkAsync(
        string chunk,
        string sourceLanguage,
        string targetLanguage,
        TranslationOptions options,
        CancellationToken cancellationToken = default);

    public abstract Task<bool> ValidateConfigurationAsync();

    public virtual async Task<int> EstimateTokenCount(string text)
    {
        var words = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
        var characters = text.Length;
        return (int)(words * 1.3 + characters * 0.1);
    }

    public virtual async Task<Dictionary<string, double>> GetLanguageConfidenceScores(string text)
    {
        var scores = new Dictionary<string, double>();

        var chinesePercentage = (double)Regex.Matches(text, @"\p{IsCJKUnifiedIdeographs}").Count / text.Length;
        if (chinesePercentage > 0)
            scores["zh"] = chinesePercentage;

        var englishPercentage = (double)Regex.Matches(text, @"[a-zA-Z]").Count / text.Length;
        if (englishPercentage > 0)
            scores["en"] = englishPercentage;

        return scores;
    }

    protected virtual TranslationResult CreateErrorResult(string originalContent, string error)
    {
        return new TranslationResult
        {
            OriginalContent = originalContent,
            Success = false,
            Error = error,
            Metrics = new TranslationMetrics
            {
                Provider = Name,
                ProcessingTime = TimeSpan.Zero
            }
        };
    }

    protected virtual async Task<List<string>> SplitIntoChunks(string text, int maxTokens)
    {
        var chunks = new List<string>();
        var sentences = Regex.Split(text, @"(?<=[.!?])\s+");
        var currentChunk = new List<string>();
        var currentTokenCount = 0;

        foreach (var sentence in sentences)
        {
            var sentenceTokens = await EstimateTokenCount(sentence);
            if (sentenceTokens > maxTokens)
            {
                var words = sentence.Split(' ');
                var currentPart = new List<string>();
                var partTokenCount = 0;

                foreach (var word in words)
                {
                    var wordTokens = await EstimateTokenCount(word);
                    if (partTokenCount + wordTokens > maxTokens)
                    {
                        chunks.Add(string.Join(" ", currentPart));
                        currentPart.Clear();
                        partTokenCount = 0;
                    }
                    currentPart.Add(word);
                    partTokenCount += wordTokens;
                }

                if (currentPart.Any())
                    chunks.Add(string.Join(" ", currentPart));
            }
            else if (currentTokenCount + sentenceTokens > maxTokens)
            {
                chunks.Add(string.Join(" ", currentChunk));
                currentChunk.Clear();
                currentChunk.Add(sentence);
                currentTokenCount = sentenceTokens;
            }
            else
            {
                currentChunk.Add(sentence);
                currentTokenCount += sentenceTokens;
            }
        }

        if (currentChunk.Any())
            chunks.Add(string.Join(" ", currentChunk));

        return chunks;
    }

    protected virtual string CreateTranslationPrompt(
        string text,
        string sourceLanguage,
        string targetLanguage,
        TranslationOptions options)
    {
        return @$"You are a professional translator with expertise in {sourceLanguage} and {targetLanguage}.
Please translate the following text from {sourceLanguage} to {targetLanguage}.
Preserve all formatting, line breaks, and special characters.
If there are technical terms or idioms, ensure they are translated appropriately for the target culture.

Text to translate:
{text}

Requirements:
- Maintain the original meaning and tone
- Preserve formatting and structure
- Ensure natural flow in the target language
- Keep technical terms accurate
- Maintain any markdown or HTML formatting if present

Translation:";
    }

    public virtual async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        TranslationOptions options,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var estimatedTokens = await EstimateTokenCount(text);
            var requestId = Guid.NewGuid().ToString("N");

            _logger.LogInformation(
                "Translation Request [{RequestId}] Started at {Timestamp}\nSource Language: {SourceLang}\nTarget Language: {TargetLang}\nEstimated Tokens: {Tokens}",
                requestId,
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                sourceLanguage,
                targetLanguage,
                estimatedTokens);

            var result = new TranslationResult
            {
                OriginalContent = text,
                Metrics = new TranslationMetrics
                {
                    Provider = Name,
                    SourceTokenCount = estimatedTokens,
                    MaxQuota = options.MaxTokensPerRequest,
                    ChapterTokenCounts = new Dictionary<string, int>()
                }
            };

            if (string.IsNullOrWhiteSpace(text))
            {
                return CreateErrorResult(text, "Empty text provided");
            }

            var chunks = await SplitIntoChunks(text, options.MaxTokensPerRequest);
            _logger.LogInformation("[{RequestId}] Split text into {ChunkCount} chunks", requestId, chunks.Count);

            var translatedChunks = new List<string>();
            var chunkIndex = 0;

            foreach (var chunk in chunks)
            {
                chunkIndex++;
                try
                {
                    var translatedChunk = await TranslateChunkAsync(chunk, sourceLanguage, targetLanguage, options, cancellationToken);
                    translatedChunks.Add(translatedChunk.TranslatedContent);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        "Response [{RequestId}] Error at {Timestamp}\nChunk {Index}/{Total}: {Error}",
                        requestId,
                        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        chunkIndex,
                        chunks.Count,
                        ex.Message);
                    return CreateErrorResult(text, $"Translation failed: {ex.Message}");
                }
            }

            stopwatch.Stop();
            result.TranslatedContent = string.Join("", translatedChunks);
            result.Success = true;
            result.Metrics.ProcessingTime = stopwatch.Elapsed;

            _logger.LogInformation(
                "Translation [{RequestId}] Completed at {Timestamp}\nTotal Chunks: {Chunks}\nTotal Time: {Duration}ms",
                requestId,
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                chunks.Count,
                stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Translation failed at {Timestamp}\nError: {Message}",
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                ex.Message);
            return CreateErrorResult(text, $"Translation failed: {ex.Message}");
        }
    }
}