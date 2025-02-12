using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.IO.Compression;
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
            _logger = logger;
            _translationProvider = translationProvider ?? throw new ArgumentNullException(nameof(translationProvider));
            _concurrencySettings = epubSettings?.Concurrency ?? throw new ArgumentNullException(nameof(epubSettings));
        }

        public async Task<EpubDocument> LoadAsync(string filePath)
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

        public async Task<bool> ValidateStructureAsync(EpubDocument document)
        {
            if (document == null)
            {
                _logger.LogError("Document is null");
                return false;
            }

            if (string.IsNullOrEmpty(document.FilePath) || !File.Exists(document.FilePath))
            {
                _logger.LogError("Invalid file path: {FilePath}", document.FilePath);
                return false;
            }

            if (!document.Chapters?.Any() ?? true)
            {
                _logger.LogError("No chapters found in EPUB document.");
                return false;
            }

            return true;
        }

        public async Task<List<EpubChapter>> ExtractChaptersAsync(EpubDocument document)
        {
            var book = await EpubReader.ReadBookAsync(document.FilePath);
            return await LoadChaptersAsync(book);
        }

        public async Task<bool> SaveTranslatedEpubAsync(EpubDocument originalDocument, string outputPath, TranslationOptions options)
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

                    var translatedContent = await RebuildChapterContentAsync(chapter);
                    await File.WriteAllTextAsync(chapterPath, translatedContent, new UTF8Encoding(false));
                }

                UpdateContentOpf(tempDir, options.TargetLanguage);

                if (originalDocument.CoverImage != null)
                {
                    await File.WriteAllBytesAsync(Path.Combine(tempDir, "cover.jpg"), originalDocument.CoverImage);
                }

                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                CreateEpubArchive(tempDir, outputPath);

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

        public async Task<bool> ValidateOutputAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                _logger.LogError("Output file path is null or empty");
                return false;
            }

            if (!File.Exists(filePath))
            {
                _logger.LogError("Output file does not exist: {FilePath}", filePath);
                return false;
            }

            return true;
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
                if (string.IsNullOrWhiteSpace(node.InnerText)) continue;

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
                if (node == null)
                {
                    var nodes = doc.DocumentNode.SelectNodes("//body//p|//body//h1|//body//h2|//body//h3|//body//h4|//body//h5|//body//h6|//body//div[not(p)]");
                    node = nodes?.FirstOrDefault(n => n.InnerText.Trim() == paragraph.Content.Trim());
                }

                if (node != null)
                {
                    node.InnerHtml = paragraph.TranslatedContent ?? paragraph.Content;
                }
            }

            var result = EnsureProperXhtmlTags(doc.DocumentNode.OuterHtml);
            return string.IsNullOrEmpty(result) ? BuildBasicChapterContent(chapter) : result;
        }

        private string EnsureProperXhtmlTags(string content)
        {
            var selfClosingTags = new[]
            {
                "img", "br", "hr", "input", "link", "meta",
                "area", "base", "col", "embed", "param",
                "source", "track", "wbr"
            };

            foreach (var tag in selfClosingTags)
            {
                var pattern = $@"<{tag}([^>]*[^/])>";
                content = Regex.Replace(content, pattern, m =>
                {
                    var attrs = m.Groups[1].Value.Trim();
                    return $"<{tag}{(attrs.Length > 0 ? " " + attrs : "")} />";
                });
            }

            return content;
        }

        private string BuildBasicChapterContent(EpubChapter chapter)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.1//EN\" \"http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd\">");
            sb.AppendLine("<html xmlns=\"http://www.w3.org/1999/xhtml\">");
            sb.AppendLine("<head>");
            sb.AppendLine("  <title>" + System.Security.SecurityElement.Escape(chapter.Title) + "</title>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            foreach (var paragraph in chapter.Paragraphs)
            {
                var content = paragraph.TranslatedContent ?? paragraph.Content;
                if (!string.IsNullOrWhiteSpace(content))
                {
                    sb.AppendLine("  <p>" + System.Security.SecurityElement.Escape(content) + "</p>");
                }
            }

            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }

        private void UpdateContentOpf(string tempDir, string targetLanguage)
        {
            var contentOpfPath = Path.Combine(tempDir, "content.opf");
            if (!File.Exists(contentOpfPath)) return;

            var content = File.ReadAllText(contentOpfPath, Encoding.UTF8);
            content = Regex.Replace(content,
                @"<dc:language>[^<]+</dc:language>",
                $"<dc:language>{targetLanguage}</dc:language>");
            File.WriteAllText(contentOpfPath, content, new UTF8Encoding(false));
        }

        private void CreateEpubArchive(string sourceDir, string outputPath)
        {
            using var zipStream = new FileStream(outputPath, FileMode.Create);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

            // Add mimetype file first, uncompressed
            var mimetypePath = Path.Combine(sourceDir, "mimetype");
            if (File.Exists(mimetypePath))
            {
                var entry = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(mimetypePath);
                fileStream.CopyTo(entryStream);
            }

            // Add all other files
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == "mimetype") continue;

                var relativePath = Path.GetRelativePath(sourceDir, file).Replace("\\", "/");
                var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(file);
                fileStream.CopyTo(entryStream);
            }
        }
    }
}
