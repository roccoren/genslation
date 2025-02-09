namespace genslation.Services;

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

    public abstract Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        TranslationOptions options,
        CancellationToken cancellationToken = default);

    public abstract Task<bool> ValidateConfigurationAsync();

    public virtual async Task<int> EstimateTokenCount(string text)
    {
        // Rough estimation based on words and characters
        // This should be overridden by providers with more accurate counting
        var words = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
        var characters = text.Length;
        return (int)(words * 1.3 + characters * 0.1);
    }

    public virtual async Task<Dictionary<string, double>> GetLanguageConfidenceScores(string text)
    {
        // Basic language detection logic
        // Should be overridden by providers with better detection
        var scores = new Dictionary<string, double>();
        
        // Check for Chinese characters
        var chinesePercentage = (double)Regex.Matches(text, @"\p{IsCJKUnifiedIdeographs}").Count / text.Length;
        if (chinesePercentage > 0)
            scores["zh"] = chinesePercentage;

        // Check for Latin characters (rough estimation for English)
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
}