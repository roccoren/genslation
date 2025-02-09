using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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

        public EpubProcessor(ILogger<EpubProcessor> logger)
        {
            _logger = logger;
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
            var result = new List<genslation.Models.EpubNavigationItem>();
            foreach (var item in navigationItems)
            {
                result.Add(new genslation.Models.EpubNavigationItem
                {
                    Title = item.Title,
                    Source = item.Link?.ContentFilePath ?? string.Empty,
                    Children = MapNavigationItems(item.NestedItems)
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
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".svg" => ResourceType.Image,
                ".ttf" or ".otf" or ".woff" or ".woff2" => ResourceType.Font,
                ".css" => ResourceType.Stylesheet,
                _ => ResourceType.Other
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
                ZipFile.CreateFromDirectory(tempDir, outputPath);

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
                    _logger.LogWarning(ex, "Failed to cleanup temporary directory: {TempDir}", tempDir);
                }
            }
        }

        private async Task<List<EpubResource>> ExtractResourcesAsync(EpubBook book)
        {
            var resources = new List<EpubResource>();
            
            foreach (var htmlFile in book.Content.Html.Local)
            {
                resources.Add(new EpubResource
                {
                    Id = Path.GetFileNameWithoutExtension(htmlFile.FilePath),
                    Path = htmlFile.FilePath,
                    Data = Encoding.UTF8.GetBytes(htmlFile.Content),
                    MimeType = "text/html",
                    Type = ResourceType.Other
                });
            }
            
            foreach (var imageFile in book.Content.Images.Local)
            {
                resources.Add(new EpubResource
                {
                    Id = Path.GetFileNameWithoutExtension(imageFile.FilePath),
                    Path = imageFile.FilePath,
                    Data = ((EpubLocalByteContentFile)imageFile).Content,
                    MimeType = imageFile.ContentMimeType,
                    Type = ResourceType.Image
                });
            }
            
            foreach (var cssFile in book.Content.Css.Local)
            {
                resources.Add(new EpubResource
                {
                    Id = Path.GetFileNameWithoutExtension(cssFile.FilePath),
                    Path = cssFile.FilePath,
                    Data = Encoding.UTF8.GetBytes(cssFile.Content),
                    MimeType = "text/css",
                    Type = ResourceType.Stylesheet
                });
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

            var textNodes = doc.DocumentNode.SelectNodes("//p|//h1|//h2|//h3|//h4|//h5|//h6|//div[normalize-space()]");
            
            if (textNodes == null) return paragraphs;

            foreach (var node in textNodes)
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
                _logger.LogWarning("Original content missing for chapter: {ChapterId}", chapter.Id);
                return BuildBasicChapterContent(chapter);
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(chapter.OriginalContent);

            // Update translatable nodes while preserving structure
            foreach (var paragraph in chapter.Paragraphs)
            {
                if (string.IsNullOrEmpty(paragraph.NodePath))
                {
                    _logger.LogWarning("Node path missing for paragraph in chapter {ChapterId}", chapter.Id);
                    continue;
                }

                var node = doc.DocumentNode.SelectSingleNode(paragraph.NodePath);
                if (node != null)
                {
                    // Preserve attributes and surrounding HTML structure
                    node.InnerHtml = paragraph.TranslatedContent ?? paragraph.Content;
                }
                else
                {
                    _logger.LogWarning("Could not find node at path {Path} in chapter {ChapterId}", paragraph.NodePath, chapter.Id);
                }
            }

            return doc.DocumentNode.OuterHtml;
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

        public async Task<bool> ValidateStructureAsync(EpubDocument document)
        {
            if (string.IsNullOrEmpty(document.FilePath) || !File.Exists(document.FilePath))
            {
                _logger.LogError("Invalid file path: {FilePath}", document.FilePath);
                return false;
            }

            if (!document.Chapters.Any())
            {
                _logger.LogError("No chapters found in EPUB document.");
                return false;
            }

            return true;
        }

        public async Task<bool> ValidateOutputAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                _logger.LogError("Output file does not exist: {FilePath}", filePath);
                return false;
            }

            return true;
        }

        public async Task<byte[]> GeneratePreviewAsync(EpubDocument document, int chapterIndex = 0)
        {
            if (chapterIndex >= document.Chapters.Count)
            {
                throw new ArgumentException("Invalid chapter index.");
            }

            var chapter = document.Chapters[chapterIndex];
            return Encoding.UTF8.GetBytes(RebuildChapterContent(chapter));
        }
    }
}
