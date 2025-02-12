using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using genslation.Configuration;
using genslation.Interfaces;
using genslation.Models;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using VersOne.Epub;

namespace genslation.Services
{
    public class EpubProcessor : IEpubProcessor
    {
        private readonly ILogger<EpubProcessor> _logger;
        private readonly ITranslationProvider _translationProvider;
        private readonly ConcurrencySettings _concurrencySettings;

        public EpubProcessor(
            ILogger<EpubProcessor> logger,
            ITranslationProvider translationProvider,
            EpubSettings epubSettings)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _translationProvider = translationProvider ?? throw new ArgumentNullException(nameof(translationProvider));
            _concurrencySettings = epubSettings?.Concurrency ?? throw new ArgumentNullException(nameof(epubSettings));
        }

        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            return text.Split(new[] { ' ', '\t', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries).Length;
        }

        public async Task<EpubDocument> LoadAsync(string filePath)
        {
            try
            {
                var book = await EpubReader.ReadBookAsync(filePath);
                return new EpubDocument
                {
                    Title = book.Title,
                    Author = string.Join("; ", book.AuthorList),
                    Language = book.Schema.Package.Metadata.Languages.FirstOrDefault()?.ToString() ?? string.Empty,
                    FilePath = filePath,
                    Chapters = await LoadChaptersAsync(book)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load EPUB file: {FilePath}", filePath);
                throw;
            }
        }

        public async Task<bool> SaveTranslatedEpubAsync(
            EpubDocument originalDocument,
            string outputPath,
            TranslationOptions options)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            try
            {
                Directory.CreateDirectory(tempDir);
                ZipFile.ExtractToDirectory(originalDocument.FilePath, tempDir);

                foreach (var chapter in originalDocument.Chapters)
                {
                    var chapterPath = Path.Combine(tempDir, chapter.OriginalPath);
                    if (!File.Exists(chapterPath)) continue;

                    try
                    {
                        var translatedContent = await RebuildChapterContentAsync(chapter);
                        await File.WriteAllTextAsync(chapterPath, translatedContent, Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process chapter: {ChapterPath}", chapterPath);
                        return false;
                    }
                }

                UpdateContentOpf(tempDir, options.TargetLanguage);

                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                ZipFile.CreateFromDirectory(tempDir, outputPath);
                return File.Exists(outputPath);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        private void UpdateContentOpf(string tempDir, string targetLanguage)
        {
            var contentOpfPath = Path.Combine(tempDir, "content.opf");
            if (!File.Exists(contentOpfPath)) return;

            var content = File.ReadAllText(contentOpfPath, Encoding.UTF8);
            content = Regex.Replace(content,
                @"<dc:language>[^<]+</dc:language>",
                $"<dc:language>{targetLanguage}</dc:language>");
            File.WriteAllText(contentOpfPath, content, Encoding.UTF8);
        }

        public async Task<List<EpubChapter>> ExtractChaptersAsync(EpubDocument document)
        {
            var book = await EpubReader.ReadBookAsync(document.FilePath);
            return await LoadChaptersAsync(book);
        }

        public Task<bool> ValidateStructureAsync(EpubDocument document)
        {
            if (document == null)
            {
                _logger.LogError("Document is null");
                return Task.FromResult(false);
            }

            if (string.IsNullOrEmpty(document.FilePath) || !File.Exists(document.FilePath))
            {
                _logger.LogError("Invalid file path: {FilePath}", document.FilePath);
                return Task.FromResult(false);
            }

            if (!document.Chapters?.Any() ?? true)
            {
                _logger.LogError("No chapters found in EPUB document.");
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public Task<bool> ValidateOutputAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                _logger.LogError("Output file path is null or empty");
                return Task.FromResult(false);
            }

            if (!File.Exists(filePath))
            {
                _logger.LogError("Output file does not exist: {FilePath}", filePath);
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public async Task<Dictionary<string, string>> ExtractMetadataAsync(string filePath)
        {
            try
            {
                var book = await EpubReader.ReadBookAsync(filePath);
                return new Dictionary<string, string>
                {
                    { "Title", book.Title },
                    { "Author", string.Join("; ", book.AuthorList) },
                    { "Language", book.Schema.Package.Metadata.Languages.FirstOrDefault()?.ToString() ?? string.Empty }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract metadata.");
                throw;
            }
        }

        public async Task<byte[]> GeneratePreviewAsync(EpubDocument document, int chapterIndex = 0)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            if (document.Chapters == null || document.Chapters.Count == 0)
            {
                throw new InvalidOperationException("Document has no chapters");
            }

            if (chapterIndex < 0 || chapterIndex >= document.Chapters.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(chapterIndex),
                    $"Invalid chapter index: {chapterIndex}. Document has {document.Chapters.Count} chapter(s).");
            }

            var chapter = document.Chapters[chapterIndex];
            var content = await RebuildChapterContentAsync(chapter);
            return Encoding.UTF8.GetBytes(content);
        }

        public async Task<EpubDocument> TranslateDocumentAsync(EpubDocument document, string targetLanguage, TranslationOptions options)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (string.IsNullOrEmpty(targetLanguage)) throw new ArgumentException("Target language cannot be empty", nameof(targetLanguage));
            if (options == null) throw new ArgumentNullException(nameof(options));

            _logger.LogInformation("Starting document translation to {TargetLanguage}", targetLanguage);

            var failedParagraphs = new ConcurrentBag<(EpubChapter Chapter, EpubParagraph Paragraph, Exception Error)>();
            var metrics = new ConcurrentDictionary<string, TranslationMetrics>();
            
            var rateLimiters = _concurrencySettings.WordCountThresholds.ToDictionary(
                kvp => kvp.Key,
                kvp => (
                    Limiter: new SemaphoreSlim(kvp.Value.Threads),
                    Description: kvp.Value.Description
                )
            );

            foreach (var chapter in document.Chapters)
            {
                var batches = await CreateParagraphBatches(chapter.Paragraphs, options.MaxTokensPerRequest);
                var tasks = batches.Select(async batch =>
                {
                    await rateLimiters[batch.Count].Limiter.WaitAsync();
                    try
                    {
                        await ProcessBatch(batch, chapter, document.Language, targetLanguage, options, metrics, failedParagraphs);
                    }
                    finally
                    {
                        rateLimiters[batch.Count].Limiter.Release();
                    }
                });

                await Task.WhenAll(tasks);
            }

            return document;
        }

        private async Task<List<EpubChapter>> LoadChaptersAsync(EpubBook book)
        {
            var chapters = new List<EpubChapter>();
            foreach (var item in book.ReadingOrder)
            {
                chapters.Add(new EpubChapter
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = Path.GetFileNameWithoutExtension(item.FilePath),
                    OriginalPath = item.FilePath,
                    OriginalContent = item.Content,
                    Paragraphs = ExtractParagraphs(item.Content)
                });
            }
            return chapters;
        }

        private List<EpubParagraph> ExtractParagraphs(string htmlContent)
        {
            var paragraphs = new List<EpubParagraph>();
            var doc = new HtmlDocument();
            doc.LoadHtml(htmlContent);

            var textNodes = doc.DocumentNode.SelectNodes("//body//p|//body//h1|//body//h2|//body//h3|//body//h4|//body//h5|//body//h6|//body//div[not(p) and normalize-space()]");
            
            if (textNodes == null) return paragraphs;

            foreach (var node in textNodes.Where(n => !string.IsNullOrWhiteSpace(n.InnerText)))
            {
                paragraphs.Add(new EpubParagraph
                {
                    Id = Guid.NewGuid().ToString(),
                    Content = node.InnerText.Trim(),
                    OriginalHtml = node.OuterHtml,
                    NodePath = node.XPath,
                    Metadata = node.Attributes.ToDictionary(attr => attr.Name, attr => attr.Value)
                });
            }

            return paragraphs;
        }

        private async Task<List<List<EpubParagraph>>> CreateParagraphBatches(List<EpubParagraph> paragraphs, int maxTokensPerBatch)
        {
            var batches = new List<List<EpubParagraph>>();
            var currentBatch = new List<EpubParagraph>();
            var currentTokenCount = 0;

            foreach (var paragraph in paragraphs)
            {
                var tokens = await _translationProvider.EstimateTokenCount(paragraph.Content);
                
                if (currentTokenCount + tokens > maxTokensPerBatch && currentBatch.Any())
                {
                    batches.Add(currentBatch);
                    currentBatch = new List<EpubParagraph>();
                    currentTokenCount = 0;
                }

                currentBatch.Add(paragraph);
                currentTokenCount += tokens;
            }

            if (currentBatch.Any())
            {
                batches.Add(currentBatch);
            }

            return batches;
        }

        private async Task ProcessBatch(
            List<EpubParagraph> batch,
            EpubChapter chapter,
            string sourceLanguage,
            string targetLanguage,
            TranslationOptions options,
            ConcurrentDictionary<string, TranslationMetrics> metrics,
            ConcurrentBag<(EpubChapter, EpubParagraph, Exception)> failedParagraphs)
        {
            foreach (var paragraph in batch)
            {
                try
                {
                    var translationResult = await _translationProvider.TranslateAsync(
                        paragraph.Content,
                        sourceLanguage,
                        targetLanguage,
                        options);

                    paragraph.TranslatedContent = translationResult.TranslatedContent;
                }
                catch (Exception ex)
                {
                    failedParagraphs.Add((chapter, paragraph, ex));
                }
            }
        }

        private async Task<string> RebuildChapterContentAsync(EpubChapter chapter)
        {
            if (string.IsNullOrEmpty(chapter.OriginalContent))
            {
                return BuildBasicChapterContent(chapter);
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(chapter.OriginalContent);

            foreach (var paragraph in chapter.Paragraphs)
            {
                var node = doc.DocumentNode.SelectSingleNode(paragraph.NodePath);
                if (node != null)
                {
                    node.InnerHtml = paragraph.TranslatedContent ?? paragraph.Content;
                }
            }

            return doc.DocumentNode.OuterHtml;
        }

        private string BuildBasicChapterContent(EpubChapter chapter)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<html><body>");
            foreach (var paragraph in chapter.Paragraphs)
            {
                sb.AppendLine($"<p>{paragraph.TranslatedContent ?? paragraph.Content}</p>");
            }
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }
    }
}