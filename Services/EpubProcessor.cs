namespace genslation.Services;

using System.Text.RegularExpressions;
using System.Linq;
using genslation.Interfaces;
using genslation.Models;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using VersOne.Epub;
using VersOne.Epub.Schema;

public class EpubProcessor : IEpubProcessor
{
    private readonly ILogger<EpubProcessor> _logger;
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public EpubProcessor(ILogger<EpubProcessor> logger)
    {
        _logger = logger;
    }

    public async Task<EpubDocument> LoadAsync(string filePath)
    {
        try
        {
            var book = await EpubReader.ReadBookAsync(filePath);
            var document = new EpubDocument
            {
                Title = GetFirstMetadataValue(book.Schema.Package.Metadata.Titles),
                Author = string.Join("; ", GetMetadataValues(book.Schema.Package.Metadata.Creators)),
                Language = GetFirstMetadataValue(book.Schema.Package.Metadata.Languages),
                FilePath = filePath
            };

            var chapters = await LoadChaptersAsync(book);
            document.Chapters.AddRange(chapters);
            document.Metadata = await ExtractMetadataAsync(filePath);
            
            return document;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ePub file: {FilePath}", filePath);
            throw;
        }
    }

    private string GetFirstMetadataValue<T>(IReadOnlyList<T> items) where T : class
    {
        if (items == null || !items.Any())
            return string.Empty;

        var item = items.FirstOrDefault();
        return item?.ToString() ?? string.Empty;
    }

    private IEnumerable<string> GetMetadataValues<T>(IReadOnlyList<T> items) where T : class
    {
        if (items == null)
            return Enumerable.Empty<string>();

        return items.Select(i => i?.ToString() ?? string.Empty)
                   .Where(s => !string.IsNullOrWhiteSpace(s));
    }

    public async Task<bool> ValidateStructureAsync(EpubDocument document)
    {
        try
        {
            if (string.IsNullOrEmpty(document.FilePath) || !File.Exists(document.FilePath))
            {
                _logger.LogError("Invalid file path: {FilePath}", document.FilePath);
                return false;
            }

            var book = await EpubReader.ReadBookAsync(document.FilePath);
            var chapters = await LoadChaptersAsync(book);

            if (!chapters.Any())
            {
                _logger.LogError("No chapters found in ePub");
                return false;
            }

            // Check for required metadata
            var metadata = book.Schema.Package.Metadata;
            if (!metadata.Titles.Any() || !metadata.Creators.Any())
            {
                _logger.LogWarning("Missing essential metadata (title or creators)");
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Structure validation failed");
            return false;
        }
    }

    public async Task<List<Models.EpubChapter>> ExtractChaptersAsync(EpubDocument document)
    {
        try
        {
            var book = await EpubReader.ReadBookAsync(document.FilePath);
            return await LoadChaptersAsync(book);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract chapters");
            throw;
        }
    }

    private async Task<List<Models.EpubChapter>> LoadChaptersAsync(EpubBook book)
    {
        var chapters = new List<Models.EpubChapter>();

        // Process all HTML content files
        foreach (var contentFile in book.ReadingOrder)
        {
            try
            {
                var content = await File.ReadAllTextAsync(contentFile.FilePath);
                if (!string.IsNullOrEmpty(content))
                {
                    var epubChapter = new Models.EpubChapter
                    {
                        Id = contentFile.FilePath,
                        Title = ExtractTitle(content),
                        Paragraphs = await ExtractParagraphs(content)
                    };
                    chapters.Add(epubChapter);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process content file: {FilePath}", contentFile.FilePath);
            }
        }

        return chapters;
    }

    public async Task<bool> SaveTranslatedEpubAsync(
        EpubDocument originalDocument,
        string outputPath,
        TranslationOptions options)
    {
        try
        {
            // For now, we'll just copy the original file
            // TODO: Implement proper ePub writing with modifications
            File.Copy(originalDocument.FilePath, outputPath, true);
            _logger.LogWarning("SaveTranslatedEpubAsync is not fully implemented. A copy of the original file was created.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save translated ePub");
            return false;
        }
    }

    public async Task<Dictionary<string, string>> ExtractMetadataAsync(string filePath)
    {
        try
        {
            var book = await EpubReader.ReadBookAsync(filePath);
            var metadata = book.Schema.Package.Metadata;
            
            var result = new Dictionary<string, string>
            {
                { "title", GetFirstMetadataValue(metadata.Titles) },
                { "language", GetFirstMetadataValue(metadata.Languages) },
                { "author", string.Join("; ", GetMetadataValues(metadata.Creators)) },
                { "publisher", string.Empty }, // Publisher not directly accessible in current version
                { "description", string.Join(Environment.NewLine, GetMetadataValues(metadata.Descriptions)) }
            };

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract metadata");
            throw;
        }
    }

    public async Task<bool> ValidateOutputAsync(string filePath)
    {
        try
        {
            var book = await EpubReader.ReadBookAsync(filePath);
            return book.ReadingOrder.Any();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Output validation failed");
            return false;
        }
    }

    public async Task<byte[]> GeneratePreviewAsync(EpubDocument document, int chapterIndex = 0)
    {
        try
        {
            if (chapterIndex >= document.Chapters.Count)
            {
                throw new ArgumentException("Invalid chapter index");
            }

            var chapter = document.Chapters[chapterIndex];
            var html = await RebuildChapterContent(chapter);
            return System.Text.Encoding.UTF8.GetBytes(html);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate preview");
            throw;
        }
    }

    private async Task<List<Models.EpubParagraph>> ExtractParagraphs(string content)
    {
        var paragraphs = new List<Models.EpubParagraph>();
        var doc = new HtmlDocument();
        doc.LoadHtml(content);

        foreach (var node in doc.DocumentNode.SelectNodes("//p|//h1|//h2|//h3|//h4|//h5|//h6|//blockquote|//pre|//table") ?? Enumerable.Empty<HtmlNode>())
        {
            var type = DetermineNodeType(node);
            var text = node.InnerText.Trim();
            
            if (!string.IsNullOrWhiteSpace(text))
            {
                paragraphs.Add(new Models.EpubParagraph
                {
                    Id = Guid.NewGuid().ToString(),
                    Content = text,
                    Type = type,
                    Language = DetectLanguage(text)
                });
            }
        }

        return paragraphs;
    }

    private string ExtractTitle(string content)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(content);
        
        var titleNode = doc.DocumentNode.SelectSingleNode("//h1") ?? 
                       doc.DocumentNode.SelectSingleNode("//title");
                       
        return titleNode?.InnerText.Trim() ?? string.Empty;
    }

    private async Task<string> RebuildChapterContent(Models.EpubChapter chapter)
    {
        var content = new System.Text.StringBuilder();
        content.AppendLine($"<h1>{chapter.Title}</h1>");

        foreach (var para in chapter.Paragraphs)
        {
            var text = string.IsNullOrEmpty(para.TranslatedContent) ? 
                      para.Content : para.TranslatedContent;
                      
            switch (para.Type)
            {
                case ParagraphType.Header:
                    content.AppendLine($"<h2>{text}</h2>");
                    break;
                case ParagraphType.Quote:
                    content.AppendLine($"<blockquote>{text}</blockquote>");
                    break;
                case ParagraphType.Code:
                    content.AppendLine($"<pre><code>{text}</code></pre>");
                    break;
                default:
                    content.AppendLine($"<p>{text}</p>");
                    break;
            }
        }

        return content.ToString();
    }

    private ParagraphType DetermineNodeType(HtmlNode node)
    {
        return node.Name switch
        {
            var name when name.StartsWith("h") => ParagraphType.Header,
            "blockquote" => ParagraphType.Quote,
            "pre" => ParagraphType.Code,
            "table" => ParagraphType.Table,
            _ => ParagraphType.Text
        };
    }

    private string DetectLanguage(string text)
    {
        // Simple language detection based on character sets
        // This should be replaced with a more sophisticated language detection library
        if (Regex.IsMatch(text, @"\p{IsCJKUnifiedIdeographs}"))
            return "zh";
        
        // Default to English if no specific scripts are detected
        return "en";
    }
}