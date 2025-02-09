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
            Directory.CreateDirectory(tempDir);

            try
            {
                // Extract original EPUB to temp directory
                ZipFile.ExtractToDirectory(originalDocument.FilePath, tempDir);

                // Update chapters with translations
                foreach (var chapter in originalDocument.Chapters)
                {
                    var chapterPath = Path.Combine(tempDir, chapter.OriginalPath);
                    if (File.Exists(chapterPath))
                    {
                        var translatedContent = RebuildChapterContent(chapter);
                        await File.WriteAllTextAsync(chapterPath, translatedContent);
                    }
                }

                // Update metadata
                var contentOpfPath = Path.Combine(tempDir, "content.opf");
                if (File.Exists(contentOpfPath))
                {
                    var content = await File.ReadAllTextAsync(contentOpfPath);
                    content = Regex.Replace(content, @"<dc:language>[^<]+</dc:language>", $"<dc:language>{options.TargetLanguage}</dc:language>");
                    await File.WriteAllTextAsync(contentOpfPath, content);
                }

                // Save cover image
                if (originalDocument.CoverImage != null)
                {
                    var coverPath = Path.Combine(tempDir, "cover.jpg");
                    await File.WriteAllBytesAsync(coverPath, originalDocument.CoverImage);
                }

                // Create output EPUB
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
                ZipFile.CreateFromDirectory(tempDir, outputPath);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save translated EPUB.");
                return false;
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        private async Task<List<EpubResource>> ExtractResourcesAsync(EpubBook book)
        {
            var resources = new List<EpubResource>();
            
            // Extract HTML content
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
            
            // Extract images
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
            
            // Extract CSS
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
                chapters.Add(new EpubChapter
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = Path.GetFileNameWithoutExtension(item.FilePath),
                    OriginalPath = item.FilePath,
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
                    Type = type
                });
            }

            return paragraphs;
        }

        private string RebuildChapterContent(EpubChapter chapter)
        {
            var content = new StringBuilder();
            content.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            content.AppendLine("<html xmlns=\"http://www.w3.org/1999/xhtml\">");
            content.AppendLine("<head>");
            content.AppendLine($"<title>{chapter.Title}</title>");
            content.AppendLine("</head>");
            content.AppendLine("<body>");
            foreach (var paragraph in chapter.Paragraphs)
            {
                content.AppendLine($"<p>{paragraph.TranslatedContent ?? paragraph.Content}</p>");
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
