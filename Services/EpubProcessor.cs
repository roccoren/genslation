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

        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            return text.Split(new[] { ' ', '\t', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries).Length;
        }
    
        public EpubProcessor(
            ILogger<EpubProcessor> logger,
            ITranslationProvider translationProvider,
            EpubSettings epubSettings)
        {
            _logger = logger;
            _translationProvider = translationProvider ?? throw new ArgumentNullException(nameof(translationProvider));
            _concurrencySettings = epubSettings?.Concurrency ?? throw new ArgumentNullException(nameof(epubSettings));
        }
    
        public async Task<EpubDocument> TranslateDocumentAsync(EpubDocument document, string targetLanguage, TranslationOptions options)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (string.IsNullOrEmpty(targetLanguage)) throw new ArgumentException("Target language cannot be empty", nameof(targetLanguage));
            if (options == null) throw new ArgumentNullException(nameof(options));

            _logger.LogInformation("Starting document translation to {TargetLanguage}", targetLanguage);

            var failedParagraphs = new ConcurrentBag<(EpubChapter Chapter, EpubParagraph Paragraph, Exception Error)>();
            var metrics = new ConcurrentDictionary<string, TranslationMetrics>();
            
            // Create rate limiters from configuration
            var rateLimiters = _concurrencySettings.WordCountThresholds.ToDictionary(
                kvp => kvp.Key,
                kvp => (
                    Limiter: new SemaphoreSlim(kvp.Value.Threads),
                    Description: kvp.Value.Description
                )
            );

            _logger.LogInformation("Initialized translation pipeline with the following concurrency levels:");
            foreach (var (threshold, level) in _concurrencySettings.WordCountThresholds.OrderBy(x => x.Key))
            {
                _logger.LogInformation(
                    "- Up to {Threshold} words: {Threads} threads ({Description})",
                    threshold == int.MaxValue ? "∞" : threshold.ToString(),
                    level.Threads,
                    level.Description);
            }

            foreach (var chapter in document.Chapters)
            {
                _logger.LogInformation("Translating chapter: {ChapterTitle}", chapter.Title);
                
                // Group paragraphs into batches based on length and token count
                var batches = await CreateParagraphBatches(chapter.Paragraphs, options.MaxTokensPerRequest);

                // Group batches by word count threshold for parallel processing
                var batchesByWordCount = batches
                    .Select(batch =>
                    {
                        var maxWords = batch.Max(p => CountWords(p.Content));
                        var threshold = _concurrencySettings.WordCountThresholds.Keys
                            .OrderBy(t => t)
                            .First(t => maxWords <= t);
                        return (Batch: batch, Threshold: threshold);
                    })
                    .GroupBy(b => b.Threshold)
                    .ToDictionary(g => g.Key, g => g.Select(b => b.Batch).ToList());

                // Process each word count group with its own concurrency limit
                foreach (var wordGroup in batchesByWordCount)
                {
                    var threshold = wordGroup.Key;
                    var batchesInGroup = wordGroup.Value;
                    var (rateLimiter, description) = rateLimiters[threshold];
                    var concurrencyLevel = _concurrencySettings.WordCountThresholds[threshold];

                    var message = $"Processing {batchesInGroup.Count} batches of {concurrencyLevel.Description} " +
                                $"with concurrency {concurrencyLevel.Threads}";
                    _logger.LogInformation(message);

                    var tasks = batchesInGroup.Select(async batch =>
                    {
                        await rateLimiter.WaitAsync();
                        try
                        {
                            await ProcessBatch(batch, chapter, document.Language, targetLanguage, options, metrics, failedParagraphs);
                            
                            // Log detailed metrics about processed batch
                            var avgWords = batch.Average(p => CountWords(p.Content));
                            _logger.LogDebug($"Completed batch processing: {description}, Average words: {avgWords:F1}");
                        }
                        finally
                        {
                            rateLimiter.Release();
                            await Task.Delay(TimeSpan.FromMilliseconds(concurrencyLevel.DelayMs));
                        }
                    });

                    // Wait for all batches in this word count group to complete
                    await Task.WhenAll(tasks);
                    
                    _logger.LogInformation(
                        "Completed processing {Description} batch group",
                        description);
                }

                // Log chapter progress
                var chapterMetrics = metrics.GetOrAdd(chapter.Title, new TranslationMetrics());
                _logger.LogInformation(
                    "Chapter {ChapterTitle} completed. Processed {TokenCount} tokens in {Time:n2} seconds",
                    chapter.Title,
                    chapterMetrics.TotalTokens,
                    chapterMetrics.ProcessingTime.TotalSeconds);
            }

            // Handle any failed paragraphs
            if (failedParagraphs.Any())
            {
                _logger.LogWarning("Retrying {Count} failed paragraphs", failedParagraphs.Count);
                await RetryFailedParagraphs(failedParagraphs, document.Language, targetLanguage, options);
            }

            // Log final metrics
            var totalProcessingTime = TimeSpan.FromSeconds(metrics.Values.Sum(m => m.ProcessingTime.TotalSeconds));
            var totalPromptTokens = metrics.Values.Sum(m => m.PromptTokens);
            var totalCompletionTokens = metrics.Values.Sum(m => m.CompletionTokens);
            var totalRetries = metrics.Values.Sum(m => m.RetryCount);

            _logger.LogInformation(
                "Document translation completed. Tokens (Prompt: {PromptTokens}, Completion: {CompletionTokens}), " +
                "Processing time: {Time:n2}s, Retries: {Retries}",
                totalPromptTokens,
                totalCompletionTokens,
                totalProcessingTime.TotalSeconds,
                totalRetries);

            return document;
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
            var sw = System.Diagnostics.Stopwatch.StartNew();

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

                    // Update metrics thread-safely
                    metrics.AddOrUpdate(
                        chapter.Title,
                        _ => new TranslationMetrics
                        {
                            PromptTokens = translationResult.Metrics.PromptTokens,
                            CompletionTokens = translationResult.Metrics.CompletionTokens,
                            ProcessingTime = translationResult.Metrics.ProcessingTime
                        },
                        (_, existing) =>
                        {
                            var updated = new TranslationMetrics
                            {
                                PromptTokens = existing.PromptTokens + translationResult.Metrics.PromptTokens,
                                CompletionTokens = existing.CompletionTokens + translationResult.Metrics.CompletionTokens,
                                ProcessingTime = existing.ProcessingTime + translationResult.Metrics.ProcessingTime
                            };
                            return updated;
                        });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to translate paragraph in chapter {ChapterTitle}", chapter.Title);
                    failedParagraphs.Add((chapter, paragraph, ex));
                }
            }

            sw.Stop();
            var batchMetrics = metrics.GetOrAdd(chapter.Title, new TranslationMetrics());
            batchMetrics.ProcessingTime += sw.Elapsed;
        }

        private async Task RetryFailedParagraphs(
            ConcurrentBag<(EpubChapter Chapter, EpubParagraph Paragraph, Exception Error)> failedParagraphs,
            string sourceLanguage,
            string targetLanguage,
            TranslationOptions options)
        {
            foreach ((EpubChapter chapter, EpubParagraph paragraph, Exception error) in failedParagraphs)
            {
                for (int retry = 0; retry < options.MaxRetries; retry++)
                {
                    try
                    {
                        _logger.LogInformation(
                            "Retry {Attempt}/{MaxRetries} for paragraph in chapter {ChapterTitle}",
                            retry + 1,
                            options.MaxRetries,
                            chapter.Title);

                        var translationResult = await _translationProvider.TranslateAsync(
                            paragraph.Content,
                            sourceLanguage,
                            targetLanguage,
                            options);

                        paragraph.TranslatedContent = translationResult.TranslatedContent;
                        break;
                    }
                    catch (Exception retryEx)
                    {
                        _logger.LogError(retryEx,
                            "Retry {Attempt}/{MaxRetries} failed for paragraph in chapter {ChapterTitle}",
                            retry + 1,
                            options.MaxRetries,
                            chapter.Title);

                        if (retry == options.MaxRetries - 1)
                        {
                            // If all retries fail, preserve original content
                            paragraph.TranslatedContent = paragraph.Content;
                            _logger.LogError("All retries failed for paragraph. Preserving original content.");
                        }
                        else
                        {
                            await Task.Delay(options.RetryDelay);
                        }
                    }
                }
            }
        }

        public async Task<List<EpubChapter>> ExtractChaptersAsync(EpubDocument document)
        {
            var book = await EpubReader.ReadBookAsync(document.FilePath);
            return await LoadChaptersAsync(book);
        }

        public async Task<EpubDocument> LoadAsync(string filePath)
        {
            try
            {
                var book = await EpubReader.ReadBookAsync(filePath);
                var document = new EpubDocument
                {
                    Title = book.Title,
                    Author = string.Join("; ", book.AuthorList),
                    Language = book.Schema.Package.Metadata.Languages.FirstOrDefault()?.ToString() ?? string.Empty,
                    FilePath = filePath,
                    CoverImage = book.CoverImage,
                    TableOfContents = MapNavigationItems(book.Navigation),
                    Resources = await ExtractResourcesAsync(book),
                    Chapters = await LoadChaptersAsync(book)
                };
                return document;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load EPUB file: {FilePath}", filePath);
                throw;
            }
        }

        private List<genslation.Models.EpubNavigationItem> MapNavigationItems(IReadOnlyList<VersOne.Epub.EpubNavigationItem> navigationItems)
        {
            if (navigationItems == null)
            {
                return new List<genslation.Models.EpubNavigationItem>();
            }

            var result = new List<genslation.Models.EpubNavigationItem>();
            foreach (var item in navigationItems)
            {
                if (item == null) continue;

                result.Add(new genslation.Models.EpubNavigationItem
                {
                    Title = item.Title ?? string.Empty,
                    Source = item.Link?.ContentFilePath ?? string.Empty,
                    Children = MapNavigationItems(item.NestedItems ?? new List<VersOne.Epub.EpubNavigationItem>())
                });
            }
            return result;
        }

        private List<EpubResource> MapResources(EpubContent content)
        {
            var resources = new List<EpubResource>();

            if (content.Html?.Local != null)
            {
                foreach (var file in content.Html.Local)
                {
                    resources.Add(new EpubResource
                    {
                        Path = file.FilePath,
                        Data = Encoding.UTF8.GetBytes(file.Content),
                        MimeType = file.ContentMimeType,
                        Type = ResourceType.Other
                    });
                }
            }

            if (content.Images?.Local != null)
            {
                foreach (var file in content.Images.Local)
                {
                    var byteFile = file as EpubLocalByteContentFile;
                    if (byteFile != null)
                    {
                        resources.Add(new EpubResource
                        {
                            Path = file.FilePath,
                            Data = byteFile.Content,
                            MimeType = file.ContentMimeType,
                            Type = ResourceType.Image
                        });
                    }
                }
            }

            if (content.Css?.Local != null)
            {
                foreach (var file in content.Css.Local)
                {
                    resources.Add(new EpubResource
                    {
                        Path = file.FilePath,
                        Data = Encoding.UTF8.GetBytes(file.Content),
                        MimeType = file.ContentMimeType,
                        Type = ResourceType.Stylesheet
                    });
                }
            }

            return resources;
        }

        private ResourceType DetermineResourceType(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension switch
            {
                // Images
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".svg" or ".webp" => ResourceType.Image,
                
                // Fonts
                ".ttf" or ".otf" or ".woff" or ".woff2" or ".eot" => ResourceType.Font,
                
                // Styles
                ".css" or ".xpgt" or ".sass" or ".scss" => ResourceType.Stylesheet,
                
                // Scripts - preserve as Other type but with proper MIME handling
                ".js" or ".mjs" => ResourceType.Other,
                
                // Media - preserve as Other type but with proper MIME handling
                ".mp3" or ".m4a" or ".ogg" or ".wav" or
                ".mp4" or ".webm" or ".ogv" => ResourceType.Other,
                
                // Misc binary files
                ".bin" or ".dat" => ResourceType.Other,
                
                // Default
                _ => ResourceType.Other
            };
        }

        private string GetMimeType(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension switch
            {
                // Images
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".svg" => "image/svg+xml",
                ".webp" => "image/webp",
                
                // Fonts
                ".ttf" => "font/ttf",
                ".otf" => "font/otf",
                ".woff" => "font/woff",
                ".woff2" => "font/woff2",
                ".eot" => "application/vnd.ms-fontobject",
                
                // Styles
                ".css" => "text/css",
                ".xpgt" => "application/vnd.adobe-page-template+xml",
                ".sass" or ".scss" => "text/x-sass",
                
                // Scripts
                ".js" or ".mjs" => "application/javascript",
                
                // Audio
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".ogg" => "audio/ogg",
                ".wav" => "audio/wav",
                
                // Video
                ".mp4" => "video/mp4",
                ".webm" => "video/webm",
                ".ogv" => "video/ogg",
                
                // Default
                _ => "application/octet-stream"
            };
        }

        public async Task<bool> SaveTranslatedEpubAsync(EpubDocument originalDocument, string outputPath, TranslationOptions options)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            _logger.LogInformation("Created temporary directory: {TempDir}", tempDir);
            
            try
            {
                Directory.CreateDirectory(tempDir);
                
                // Verify temp directory permissions
                try
                {
                    var testFile = Path.Combine(tempDir, "test.txt");
                    await File.WriteAllTextAsync(testFile, "test", Encoding.UTF8);
                    File.Delete(testFile);
                    _logger.LogInformation("Temporary directory write permissions verified");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to verify temporary directory permissions: {TempDir}", tempDir);
                    return false;
                }

                // Extract original EPUB
                _logger.LogInformation("Extracting EPUB to: {FilePath}", originalDocument.FilePath);
                ZipFile.ExtractToDirectory(originalDocument.FilePath, tempDir);

                // Log extracted files
                var extractedFiles = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories);
                _logger.LogInformation("Extracted {Count} files:", extractedFiles.Length);
                foreach (var file in extractedFiles)
                {
                    _logger.LogDebug("- {FilePath} ({Size} bytes)",
                        Path.GetRelativePath(tempDir, file),
                        new FileInfo(file).Length);
                }

                // Update chapters with translations
                foreach (var chapter in originalDocument.Chapters)
                {
                    var chapterPath = Path.Combine(tempDir, chapter.OriginalPath);
                    _logger.LogInformation("Processing chapter: {ChapterPath}", chapter.OriginalPath);

                    if (!File.Exists(chapterPath))
                    {
                        _logger.LogError("Chapter file not found: {ChapterPath}", chapterPath);
                        continue;
                    }

                    try
                    {
                        // Read original content for verification
                        var originalContent = await File.ReadAllTextAsync(chapterPath, Encoding.UTF8);
                        _logger.LogDebug("Original chapter content sample:\n{Content}",
                            originalContent.Length > 500 ? originalContent.Substring(0, 500) + "..." : originalContent);

                        // Generate translated content
                        var translatedContent = RebuildChapterContent(chapter);
                        _logger.LogDebug("Translated chapter content sample:\n{Content}",
                            translatedContent.Length > 500 ? translatedContent.Substring(0, 500) + "..." : translatedContent);

                        // Write with explicit UTF-8 encoding
                        await File.WriteAllTextAsync(chapterPath, translatedContent, new UTF8Encoding(false));
                        
                        // Verify written content
                        var verificationContent = await File.ReadAllTextAsync(chapterPath, Encoding.UTF8);
                        if (verificationContent != translatedContent)
                        {
                            _logger.LogError("Content verification failed for chapter: {ChapterPath}", chapter.OriginalPath);
                            _logger.LogDebug("Expected length: {Expected}, Actual length: {Actual}",
                                translatedContent.Length, verificationContent.Length);
                            return false;
                        }
                        
                        _logger.LogInformation("Successfully processed chapter: {ChapterPath}", chapter.OriginalPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process chapter: {ChapterPath}", chapterPath);
                        return false;
                    }
                }

                // Update metadata with explicit encoding
                var contentOpfPath = Path.Combine(tempDir, "content.opf");
                if (File.Exists(contentOpfPath))
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(contentOpfPath, Encoding.UTF8);
                        _logger.LogDebug("Original content.opf:\n{Content}", content);

                        content = Regex.Replace(content,
                            @"<dc:language>[^<]+</dc:language>",
                            $"<dc:language>{options.TargetLanguage}</dc:language>");
                        
                        await File.WriteAllTextAsync(contentOpfPath, content, new UTF8Encoding(false));
                        _logger.LogInformation("Updated content.opf language to: {Language}", options.TargetLanguage);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to update content.opf");
                        return false;
                    }
                }

                // Save cover image if present
                if (originalDocument.CoverImage != null)
                {
                    var coverPath = Path.Combine(tempDir, "cover.jpg");
                    try
                    {
                        await File.WriteAllBytesAsync(coverPath, originalDocument.CoverImage);
                        _logger.LogInformation("Saved cover image: {Size} bytes", originalDocument.CoverImage.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to save cover image");
                        return false;
                    }
                }

                // Log final temporary directory contents
                var finalFiles = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories);
                _logger.LogInformation("Final temporary directory contents ({Count} files):", finalFiles.Length);
                foreach (var file in finalFiles)
                {
                    _logger.LogDebug("- {FilePath} ({Size} bytes)",
                        Path.GetRelativePath(tempDir, file),
                        new FileInfo(file).Length);
                }

                // Create output EPUB
                if (File.Exists(outputPath))
                {
                    _logger.LogInformation("Removing existing output file: {OutputPath}", outputPath);
                    File.Delete(outputPath);
                }

                _logger.LogInformation("Creating EPUB from temporary directory");
                using (var zipToOpen = new FileStream(outputPath, FileMode.Create))
                {
                    using (var archive = new ZipArchive(zipToOpen, ZipArchiveMode.Create))
                    {
                        // Add mimetype file uncompressed
                        var mimetypePath = Path.Combine(tempDir, "mimetype");
                        if (File.Exists(mimetypePath))
                        {
                            var mimetypeEntry = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
                            using (var mimetypeStream = mimetypeEntry.Open())
                            using (var fileStream = File.OpenRead(mimetypePath))
                            {
                                fileStream.CopyTo(mimetypeStream);
                            }
                        }
                
                        // Add all other files
                        foreach (var file in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
                        {
                            if (Path.GetFileName(file) == "mimetype") continue;
                
                            var relativePath = Path.GetRelativePath(tempDir, file).Replace("\\", "/");
                            var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
                            using (var entryStream = entry.Open())
                            using (var fileStream = File.OpenRead(file))
                            {
                                fileStream.CopyTo(entryStream);
                            }
                        }
                    }
                }

                // Verify output file
                if (!File.Exists(outputPath))
                {
                    _logger.LogError("Output file was not created: {OutputPath}", outputPath);
                    return false;
                }

                var outputFileInfo = new FileInfo(outputPath);
                _logger.LogInformation("Successfully created EPUB: {OutputPath} ({Size} bytes)",
                    outputPath,
                    outputFileInfo.Length);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save translated EPUB");
                return false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                        _logger.LogInformation("Cleaned up temporary directory: {TempDir}", tempDir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up temporary directory: {TempDir}. Manual cleanup may be required.", tempDir);
                }
            }
        }

        private async Task<List<EpubResource>> ExtractResourcesAsync(EpubBook book)
        {
            var resources = new List<EpubResource>();
            var processedPaths = new HashSet<string>();

            // Process standard content through EPUB reader
            void AddResource(EpubLocalContentFile file, ResourceType defaultType)
            {
                if (processedPaths.Contains(file.FilePath)) return;
                processedPaths.Add(file.FilePath);

                var resourceType = DetermineResourceType(file.FilePath);
                byte[] data;
                if (file is EpubLocalByteContentFile byteFile)
                {
                    data = byteFile.Content;
                }
                else
                {
                    data = Encoding.UTF8.GetBytes(((EpubLocalTextContentFile)file).Content);
                }

                resources.Add(new EpubResource
                {
                    Id = Path.GetFileNameWithoutExtension(file.FilePath),
                    Path = file.FilePath,
                    Data = data,
                    MimeType = GetMimeType(file.FilePath) ?? file.ContentMimeType,
                    Type = resourceType != ResourceType.Other ? resourceType : defaultType
                });
            }

            // Process standard content types
            foreach (var file in book.Content.Html.Local)
            {
                AddResource(file, ResourceType.Other);
            }

            foreach (var file in book.Content.Images.Local)
            {
                if (file is EpubLocalByteContentFile)
                {
                    AddResource(file, ResourceType.Image);
                }
            }

            foreach (var file in book.Content.Css.Local)
            {
                AddResource(file, ResourceType.Stylesheet);
            }

            // Preserve any additional files from the EPUB archive
            if (string.IsNullOrEmpty(book?.FilePath))
            {
                _logger.LogWarning("Book file path is null or empty");
                return resources;
            }

            if (!File.Exists(book.FilePath))
            {
                _logger.LogWarning("Book file does not exist at path: {FilePath}", book.FilePath);
                return resources;
            }

            try
            {
                using var archive = ZipFile.OpenRead(book.FilePath);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) || processedPaths.Contains(entry.FullName))
                    {
                        continue;
                    }

                    using var stream = entry.Open();
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    var data = ms.ToArray();

                    var resourceType = DetermineResourceType(entry.FullName);
                    resources.Add(new EpubResource
                    {
                        Id = Path.GetFileNameWithoutExtension(entry.FullName),
                        Path = entry.FullName,
                        Data = data,
                        MimeType = GetMimeType(entry.FullName),
                        Type = resourceType
                    });

                    processedPaths.Add(entry.FullName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing additional files from EPUB archive");
            }

            return resources;
        }

        private async Task<List<EpubChapter>> LoadChaptersAsync(EpubBook book)
        {
            var chapters = new List<EpubChapter>();
            foreach (var item in book.ReadingOrder)
            {
                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(item.Content);
                
                chapters.Add(new EpubChapter
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = Path.GetFileNameWithoutExtension(item.FilePath),
                    OriginalPath = item.FilePath,
                    OriginalContent = item.Content, // Store the complete original HTML
                    Paragraphs = ParseParagraphs(item.Content)
                });
            }
            return chapters;
        }

        private List<EpubParagraph> ParseParagraphs(string htmlContent)
        {
            var paragraphs = new List<EpubParagraph>();
            var doc = new HtmlDocument();
            doc.LoadHtml(htmlContent);

            var textNodes = doc.DocumentNode.SelectNodes("//body//p|//body//h1|//body//h2|//body//h3|//body//h4|//body//h5|//body//h6|//body//div[not(p) and normalize-space()]");
            
            if (textNodes == null) return paragraphs;

            foreach (var node in textNodes.Where(n => !string.IsNullOrWhiteSpace(n.InnerText)))
            {
                if (string.IsNullOrWhiteSpace(node.InnerText)) continue;

                var type = node.Name.ToLower() switch
                {
                    "h1" => ParagraphType.Heading1,
                    "h2" => ParagraphType.Heading2,
                    "h3" => ParagraphType.Heading3,
                    "h4" => ParagraphType.Heading4,
                    "h5" => ParagraphType.Heading5,
                    "h6" => ParagraphType.Heading6,
                    _ => ParagraphType.Text
                };

                paragraphs.Add(new EpubParagraph
                {
                    Content = node.InnerText.Trim(),
                    OriginalHtml = node.OuterHtml,
                    Type = type,
                    NodePath = node.XPath // Store XPath for later reconstruction
                });
            }

            return paragraphs;
        }

        private string RebuildChapterContent(EpubChapter chapter)
        {
            if (string.IsNullOrEmpty(chapter.OriginalContent))
            {
                _logger.LogWarning("Original content is empty for chapter {ChapterTitle}. Using basic content template.", chapter.Title);
                return BuildBasicChapterContent(chapter);
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(chapter.OriginalContent);

            // Update translatable nodes while preserving structure
            foreach (var paragraph in chapter.Paragraphs)
            {
                try
                {
                    if (string.IsNullOrEmpty(paragraph.NodePath))
                    {
                        _logger.LogWarning("Missing node path for paragraph in chapter {ChapterTitle}. Content: {Content}",
                            chapter.Title,
                            paragraph.Content?.Substring(0, Math.Min(50, paragraph.Content?.Length ?? 0)));
                        continue;
                    }

                    // Try exact path first
                    var node = doc.DocumentNode.SelectSingleNode(paragraph.NodePath);
                    
                    // If not found, try searching by content
                    if (node == null)
                    {
                        var nodes = doc.DocumentNode.SelectNodes("//body//p|//body//h1|//body//h2|//body//h3|//body//h4|//body//h5|//body//h6|//body//div[not(p)]");
                        if (nodes != null)
                        {
                            node = nodes.FirstOrDefault(n => n.InnerText.Trim() == paragraph.Content.Trim());
                        }
                    }

                    if (node != null)
                    {
                        // Preserve attributes and surrounding HTML structure
                        node.InnerHtml = paragraph.TranslatedContent ?? paragraph.Content;
                    }
                    else
                    {
                        _logger.LogWarning("Node not found for content in chapter {ChapterTitle}, falling back to basic template",
                            chapter.Title);
                        return BuildBasicChapterContent(chapter);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating node content in chapter {ChapterTitle}", chapter.Title);
                    return BuildBasicChapterContent(chapter);
                }
            }

            var result = EnsureProperXhtmlTags(doc.DocumentNode.OuterHtml);
            
            if (string.IsNullOrEmpty(result))
            {
                _logger.LogWarning("Failed to rebuild chapter content for {ChapterTitle}. Using basic content template.", chapter.Title);
                return BuildBasicChapterContent(chapter);
            }

            return result;
        }

        private string EnsureProperXhtmlTags(string content)
        {
            // Ensure link tags are properly self-closing
            content = Regex.Replace(content, @"<link([^>]+?)>(?:</link>)?", "<link$1 />");
        
            // Ensure all tags have corresponding closure tags
            var doc = new HtmlDocument();
            doc.LoadHtml(content);
        
            // Iterate through all nodes to find unclosed tags
            foreach (var node in doc.DocumentNode.DescendantsAndSelf())
            {
                // List of known self-closing tags
                var selfClosingTags = new HashSet<string> { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr" };
        
                if (!node.Closed && !selfClosingTags.Contains(node.Name.ToLower()))
                {
                    // Append the appropriate closing tag
                    content += $"</{node.Name}>";
                }
            }
        
            return content;
        }

        private string BuildBasicChapterContent(EpubChapter chapter)
        {
            var content = new StringBuilder();
            content.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            content.AppendLine("<html xmlns=\"http://www.w3.org/1999/xhtml\">");
            content.AppendLine("<head>");
            content.AppendLine($"<title>{chapter.Title}</title>");
            content.AppendLine("<link rel=\"stylesheet\" type=\"text/css\" href=\"styles.css\" />");
            content.AppendLine("</head>");
            content.AppendLine("<body>");
            foreach (var paragraph in chapter.Paragraphs)
            {
                var tag = paragraph.Type switch
                {
                    ParagraphType.Heading1 => "h1",
                    ParagraphType.Heading2 => "h2",
                    ParagraphType.Heading3 => "h3",
                    ParagraphType.Heading4 => "h4",
                    ParagraphType.Heading5 => "h5",
                    ParagraphType.Heading6 => "h6",
                    _ => "p"
                };
                content.AppendLine($"<{tag}>{paragraph.TranslatedContent ?? paragraph.Content}</{tag}>");
            }
            content.AppendLine("</body>");
            content.AppendLine("</html>");
            return content.ToString();
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

        public Task<byte[]> GeneratePreviewAsync(EpubDocument document, int chapterIndex = 0)
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
            return Task.FromResult(Encoding.UTF8.GetBytes(RebuildChapterContent(chapter)));
        }
    }
}
