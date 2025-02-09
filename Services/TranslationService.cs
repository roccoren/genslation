namespace genslation.Services;

using System.Collections.Concurrent;
using genslation.Models;
using genslation.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

public class TranslationService
{
    private readonly ILogger<TranslationService> _logger;
    private readonly Kernel _kernel;
    private readonly ITranslationProvider _translationProvider;
    private readonly ITranslationMemoryService _translationMemory;
    private readonly TranslationOptions _defaultOptions;
    private readonly SemaphoreSlim _semaphore;
    private readonly ConcurrentDictionary<string, DateTime> _lastRequestTimes;

    public TranslationService(
        ILogger<TranslationService> logger,
        Kernel kernel,
        ITranslationProvider translationProvider,
        ITranslationMemoryService translationMemory,
        TranslationOptions defaultOptions)
    {
        _logger = logger;
        _kernel = kernel;
        _translationProvider = translationProvider;
        _translationMemory = translationMemory;
        _defaultOptions = defaultOptions;
        _semaphore = new SemaphoreSlim(1, 1);
        _lastRequestTimes = new ConcurrentDictionary<string, DateTime>();
    }

    public async Task<EpubDocument> TranslateDocumentAsync(
        EpubDocument document,
        TranslationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= _defaultOptions;
        var translatedDocument = new EpubDocument
        {
            Title = document.Title,
            Author = document.Author,
            Language = options.TargetLanguage,
            FilePath = document.FilePath,
            Metadata = new Dictionary<string, string>(document.Metadata)
        };

        foreach (var chapter in document.Chapters)
        {
            var translatedChapter = await TranslateChapterAsync(chapter, options, cancellationToken);
            translatedDocument.Chapters.Add(translatedChapter);
            
            _logger.LogInformation(
                "Translated chapter {ChapterId}: {Title}", 
                chapter.Id, 
                chapter.Title);
        }

        return translatedDocument;
    }

    private async Task<EpubChapter> TranslateChapterAsync(
        EpubChapter chapter,
        TranslationOptions options,
        CancellationToken cancellationToken)
    {
        var translatedChapter = new EpubChapter
        {
            Id = chapter.Id,
            Title = await TranslateTextWithMemoryAsync(chapter.Title, options, cancellationToken),
            Metadata = new Dictionary<string, string>(chapter.Metadata),
            OriginalPath = chapter.OriginalPath,
            OriginalContent = chapter.OriginalContent,
            StyleAttributes = new Dictionary<string, string>(chapter.StyleAttributes)
        };

        foreach (var paragraph in chapter.Paragraphs)
        {
            var translatedParagraph = await TranslateParagraphAsync(paragraph, options, cancellationToken);
            translatedChapter.Paragraphs.Add(translatedParagraph);
        }

        return translatedChapter;
    }

    private async Task<EpubParagraph> TranslateParagraphAsync(
        EpubParagraph paragraph,
        TranslationOptions options,
        CancellationToken cancellationToken)
    {
        var translatedParagraph = new EpubParagraph
        {
            Id = paragraph.Id,
            Content = paragraph.Content,
            Type = paragraph.Type,
            Language = options.TargetLanguage,
            Metadata = new Dictionary<string, string>(paragraph.Metadata),
            Tags = new List<string>(paragraph.Tags),
            OriginalHtml = paragraph.OriginalHtml,
            NodePath = paragraph.NodePath,
            Styles = new List<EpubTextStyle>(paragraph.Styles)
        };

        try
        {
            translatedParagraph.TranslatedContent = 
                await TranslateTextWithMemoryAsync(paragraph.Content, options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to translate paragraph {Id}", paragraph.Id);
            translatedParagraph.TranslatedContent = paragraph.Content;
        }

        return translatedParagraph;
    }

    private async Task<string> TranslateTextWithMemoryAsync(
        string text,
        TranslationOptions options,
        CancellationToken cancellationToken)
    {
        // Check translation memory first
        if (options.EnableTranslationMemory)
        {
            var memoryEntry = await _translationMemory.FindMatchAsync(
                text,
                options.SourceLanguage,
                options.TargetLanguage);

            if (memoryEntry != null)
            {
                _logger.LogInformation("Found translation in memory for text: {Text}", text);
                return memoryEntry.TranslatedText;
            }
        }

        // Apply rate limiting
        await ApplyRateLimitingAsync();

        // Translate the text
        var result = await _translationProvider.TranslateAsync(
            text,
            options.SourceLanguage,
            options.TargetLanguage,
            options,
            cancellationToken);

        if (!result.Success)
        {
            throw new Exception($"Translation failed: {result.Error}");
        }

        // Store in translation memory
        if (options.EnableTranslationMemory && result.Success)
        {
            await _translationMemory.StoreTranslationAsync(new TranslationMemoryEntry
            {
                SourceText = text,
                TranslatedText = result.TranslatedContent,
                SourceLanguage = options.SourceLanguage,
                TargetLanguage = options.TargetLanguage,
                CreatedAt = DateTime.UtcNow,
                LastUsed = DateTime.UtcNow,
                UseCount = 1,
                QualityScore = result.QualityScore
            });
        }

        return result.TranslatedContent;
    }

    private async Task ApplyRateLimitingAsync()
    {
        var providerKey = _translationProvider.Name;
        var now = DateTime.UtcNow;
        var lastRequestTime = _lastRequestTimes.GetOrAdd(providerKey, now);
        var timeSinceLastRequest = now - lastRequestTime;

        if (timeSinceLastRequest < _defaultOptions.RetryDelay)
        {
            var delayTime = _defaultOptions.RetryDelay - timeSinceLastRequest;
            await Task.Delay(delayTime);
        }

        await _semaphore.WaitAsync();
        try
        {
            _lastRequestTimes[providerKey] = DateTime.UtcNow;
        }
        finally
        {
            _semaphore.Release();
        }
    }
}