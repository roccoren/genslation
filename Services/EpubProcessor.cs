using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using genslation.Interfaces;
using genslation.Models;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using VersOne.Epub;

namespace genslation.Services
{
    public class EpubProcessor : IEpubProcessor
    {
        private readonly ITranslationProvider _translationProvider;
        private readonly ILogger<EpubProcessor> _logger;

        public EpubProcessor(ILogger<EpubProcessor> logger, ITranslationProvider translationProvider)
        {
            _logger = logger;
            _translationProvider = translationProvider;
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
                    Chapters = await ExtractChaptersAsync(book)
                };
                return document;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load EPUB file: {FilePath}", filePath);
                throw;
            }
        }

        private async Task<List<EpubChapter>> ExtractChaptersAsync(EpubBook book)
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

            // Select all elements that might contain translatable text
            var textNodes = doc.DocumentNode.SelectNodes(
                "//text()[normalize-space()][not(ancestor::script)][not(ancestor::style)]/.."
            );
            
            if (textNodes == null) return paragraphs;

            foreach (var node in textNodes)
            {
                if (string.IsNullOrWhiteSpace(node.InnerText)) continue;

                // Store all attributes for preservation
                var attributes = node.Attributes.ToDictionary(
                    attr => attr.Name,
                    attr => attr.Value
                );

                paragraphs.Add(new EpubParagraph
                {
                    Id = Guid.NewGuid().ToString(),
                    Content = node.InnerText.Trim(),
                    OriginalHtml = node.OuterHtml,
                    NodePath = node.XPath,
                    Metadata = attributes
                });
            }

            return paragraphs;
        }

        public async Task<bool> SaveTranslatedEpubAsync(EpubDocument document, string outputPath, TranslationOptions options)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(tempDir);
                ZipFile.ExtractToDirectory(document.FilePath, tempDir);

                foreach (var chapter in document.Chapters)
                {
                    var chapterPath = Path.Combine(tempDir, chapter.OriginalPath);
                    if (!File.Exists(chapterPath)) continue;

                    var translatedContent = await RebuildChapterContentAsync(chapter);
                    await File.WriteAllTextAsync(chapterPath, translatedContent, new UTF8Encoding(false));
                }

                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                using (var zipStream = new FileStream(outputPath, FileMode.Create))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    // First file must be mimetype with no compression
                    var mimetypeEntry = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
                    using (var writer = new StreamWriter(mimetypeEntry.Open()))
                    {
                        writer.Write("application/epub+zip");
                    }

                    // Add all other files
                    foreach (var filePath in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
                    {
                        if (Path.GetFileName(filePath).ToLower() == "mimetype") continue;

                        var relativePath = Path.GetRelativePath(tempDir, filePath).Replace("\\", "/");
                        var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
                        using (var entryStream = entry.Open())
                        using (var fileStream = File.OpenRead(filePath))
                        {
                            fileStream.CopyTo(entryStream);
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save translated EPUB");
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

        private async Task<string> RebuildChapterContentAsync(EpubChapter chapter)
        {
            if (string.IsNullOrEmpty(chapter.OriginalContent))
            {
                return string.Empty;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(chapter.OriginalContent);

            foreach (var paragraph in chapter.Paragraphs)
            {
                if (string.IsNullOrEmpty(paragraph.NodePath)) continue;

                var node = doc.DocumentNode.SelectSingleNode(paragraph.NodePath);
                if (node != null)
                {
                    // Restore attributes
                    foreach (var attr in paragraph.Metadata)
                    {
                        node.SetAttributeValue(attr.Key, attr.Value);
                    }

                    // Update text content while preserving HTML structure
                    node.InnerHtml = paragraph.TranslatedContent ?? paragraph.Content;
                }
            }

            return EnsureProperXhtmlTags(doc.DocumentNode.OuterHtml);
        }

        private string EnsureProperXhtmlTags(string content)
        {
            var selfClosingTags = new[]
            {
                "img", "br", "hr", "input", "link", "meta",
                "area", "base", "col", "embed", "param",
                "source", "track", "wbr"
            };

            // Convert all self-closing tags to proper XHTML format
            foreach (var tag in selfClosingTags)
            {
                // Match tags with or without attributes, not already properly closed
                var pattern = $@"<{tag}([^>]*[^/])>";
                content = Regex.Replace(content, pattern, m =>
                {
                    var attrs = m.Groups[1].Value.Trim();
                    return $"<{tag}{(attrs.Length > 0 ? " " + attrs : "")} />";
                });
            }

            return content;
        }

        public async Task<List<EpubChapter>> ExtractChaptersAsync(EpubDocument document)
        {
            var book = await EpubReader.ReadBookAsync(document.FilePath);
            return await ExtractChaptersAsync(book);
        }

        public async Task<bool> ValidateStructureAsync(EpubDocument document)
        {
            return !string.IsNullOrEmpty(document.FilePath) && 
                   File.Exists(document.FilePath) && 
                   document.Chapters.Any();
        }

        public async Task<bool> ValidateOutputAsync(string filePath)
        {
            return File.Exists(filePath);
        }

        public async Task<Dictionary<string, string>> ExtractMetadataAsync(string filePath)
        {
            var book = await EpubReader.ReadBookAsync(filePath);
            return new Dictionary<string, string>
            {
                { "Title", book.Title },
                { "Author", string.Join("; ", book.AuthorList) },
                { "Language", book.Schema.Package.Metadata.Languages.FirstOrDefault()?.ToString() ?? string.Empty }
            };
        }

        public async Task<byte[]> GeneratePreviewAsync(EpubDocument document, int chapterIndex = 0)
        {
            if (chapterIndex >= document.Chapters.Count)
            {
                throw new ArgumentException("Invalid chapter index.");
            }

            var chapter = document.Chapters[chapterIndex];
            var translatedContent = await RebuildChapterContentAsync(chapter);
            return Encoding.UTF8.GetBytes(translatedContent);
        }
    }
}
