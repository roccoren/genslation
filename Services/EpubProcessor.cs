namespace genslation.Services;

using System.Text.RegularExpressions;
using System.Linq;
using System.IO.Compression;
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
        if (book == null)
        {
            throw new ArgumentNullException(nameof(book));
        }

        if (string.IsNullOrEmpty(book.FilePath))
        {
            throw new ArgumentException("Book file path is null or empty", nameof(book));
        }

        _logger.LogDebug("Starting to load chapters from epub: {FilePath}", book.FilePath);
        var chapters = new List<Models.EpubChapter>();
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            _logger.LogDebug("Creating temporary directory: {TempDir}", tempDir);
            Directory.CreateDirectory(tempDir);
            
            _logger.LogDebug("Extracting epub to temporary directory");
            System.IO.Compression.ZipFile.ExtractToDirectory(book.FilePath, tempDir);
            _logger.LogDebug("Successfully extracted epub contents");

            foreach (var readingItem in book.ReadingOrder)
            {
                try
                {
                    if (readingItem == null)
                    {
                        _logger.LogWarning("Skipping null reading item");
                        continue;
                    }

                    _logger.LogInformation("Processing content file: {FilePath}", readingItem.FilePath);
                    _logger.LogDebug("Reading item details - FilePath: {FilePath}", readingItem.FilePath);

                    var relativePath = readingItem.FilePath?.TrimStart('/') ?? string.Empty;
                    if (string.IsNullOrEmpty(relativePath))
                    {
                        _logger.LogWarning("Skipping reading item with empty path");
                        continue;
                    }

                    var possiblePaths = new[]
                    {
                        Path.Combine(tempDir, relativePath),
                        Path.Combine(tempDir, "EPUB", relativePath),
                        Path.Combine(tempDir, "OPS", relativePath),
                        Path.Combine(tempDir, "OEBPS", relativePath)
                    };

                    string? content = null;
                    foreach (var path in possiblePaths)
                    {
                        _logger.LogDebug("Checking path: {Path}", path);
                        if (File.Exists(path))
                        {
                            _logger.LogInformation("Found content at: {Path}", path);
                            content = await File.ReadAllTextAsync(path);
                            break;
                        }
                    }

                    if (!string.IsNullOrEmpty(content))
                    {
                        _logger.LogDebug("Processing content with length: {Length}", content.Length);
                        var title = ExtractTitle(content);
                        _logger.LogDebug("Extracted title: {Title}", title);

                        var paragraphs = ExtractParagraphs(content);
                        _logger.LogDebug("Extracted {Count} paragraphs", paragraphs.Count);

                        var epubChapter = new Models.EpubChapter
                        {
                            Id = readingItem.FilePath,
                            Title = title,
                            Paragraphs = paragraphs
                        };
                        chapters.Add(epubChapter);
                        _logger.LogInformation("Successfully processed chapter: {ChapterId}", epubChapter.Id);
                    }
                    else
                    {
                        _logger.LogWarning("Could not find or read content for: {FilePath}", readingItem.FilePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process content file: {FilePath}", readingItem.FilePath);
                }
            }

            _logger.LogDebug("Completed processing {Count} chapters", chapters.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process chapters");
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    _logger.LogDebug("Cleaning up temporary directory: {TempDir}", tempDir);
                    Directory.Delete(tempDir, true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temporary directory: {TempDir}", tempDir);
            }
        }

        return chapters;
    }

    public async Task<bool> SaveTranslatedEpubAsync(
        EpubDocument originalDocument,
        string outputPath,
        TranslationOptions options)
    {
        ArgumentNullException.ThrowIfNull(originalDocument);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentNullException.ThrowIfNull(options);

        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            _logger.LogDebug("Creating temporary directory for epub modification: {TempDir}", tempDir);
            Directory.CreateDirectory(tempDir);

            // Extract the original epub
            _logger.LogDebug("Extracting original epub to temp directory");
            ZipFile.ExtractToDirectory(originalDocument.FilePath, tempDir);

            // Update content files with translations
            foreach (var chapter in originalDocument.Chapters)
            {
                try
                {
                    var chapterPath = Path.Combine(tempDir, chapter.Id);
                    if (File.Exists(chapterPath))
                    {
                        _logger.LogDebug("Updating translated content for chapter: {ChapterId}", chapter.Id);
                        var translatedContent = RebuildChapterContent(chapter);
                        await File.WriteAllTextAsync(chapterPath, translatedContent);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update chapter: {ChapterId}", chapter.Id);
                }
            }

            // Update language in content.opf if exists
            var contentOpfPath = Path.Combine(tempDir, "EPUB", "content.opf");
            if (File.Exists(contentOpfPath))
            {
                _logger.LogDebug("Updating content.opf with target language");
                var content = await File.ReadAllTextAsync(contentOpfPath);
                content = Regex.Replace(content,
                    @"<dc:language>[^<]+</dc:language>",
                    $"<dc:language>{options.TargetLanguage}</dc:language>");
                await File.WriteAllTextAsync(contentOpfPath, content);
            }

            // Create new epub file
            _logger.LogDebug("Creating new epub file with translated content");
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            ZipFile.CreateFromDirectory(tempDir, outputPath);

            _logger.LogInformation("Successfully saved translated epub to: {OutputPath}", outputPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save translated ePub");
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    _logger.LogDebug("Cleaning up temporary directory");
                    Directory.Delete(tempDir, true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temporary directory: {TempDir}", tempDir);
            }
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
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        try
        {
            _logger.LogDebug("Validating output file: {FilePath}", filePath);
            var book = await EpubReader.ReadBookAsync(filePath);
            var hasContent = book.ReadingOrder.Any();
            _logger.LogDebug("Output validation result: {HasContent}", hasContent);
            return hasContent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Output validation failed for: {FilePath}", filePath);
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
            var html = RebuildChapterContent(chapter);
            return System.Text.Encoding.UTF8.GetBytes(html);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate preview");
            throw;
        }
    }

    private List<Models.EpubParagraph> ExtractParagraphs(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        
        var paragraphs = new List<Models.EpubParagraph>();
        var doc = new HtmlDocument();
        doc.LoadHtml(content);

        var nodes = doc.DocumentNode.SelectNodes("//p|//h1|//h2|//h3|//h4|//h5|//h6|//blockquote|//pre|//table");
        if (nodes != null)
        {
            foreach (var node in nodes)
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
        }

        return paragraphs;
    }

    private string ExtractTitle(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        
        var doc = new HtmlDocument();
        doc.LoadHtml(content);
        
        var titleNode = doc.DocumentNode.SelectSingleNode("//h1") ??
                       doc.DocumentNode.SelectSingleNode("//title");
                       
        return titleNode?.InnerText.Trim() ?? string.Empty;
    }

    private string RebuildChapterContent(Models.EpubChapter chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        
        var content = new System.Text.StringBuilder();
        content.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        content.AppendLine("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.1//EN\" \"http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd\">");
        content.AppendLine("<html xmlns=\"http://www.w3.org/1999/xhtml\">");
        content.AppendLine("<head>");
        content.AppendLine("  <title>" + System.Security.SecurityElement.Escape(chapter.Title) + "</title>");
        content.AppendLine("  <meta http-equiv=\"Content-Type\" content=\"application/xhtml+xml; charset=utf-8\"/>");
        content.AppendLine("</head>");
        content.AppendLine("<body>");

        content.AppendLine($"  <h1>{System.Security.SecurityElement.Escape(chapter.Title)}</h1>");

        foreach (var para in chapter.Paragraphs)
        {
            var text = System.Security.SecurityElement.Escape(
                string.IsNullOrEmpty(para.TranslatedContent) ? para.Content : para.TranslatedContent
            );
                        
            switch (para.Type)
            {
                case ParagraphType.Header:
                    content.AppendLine($"  <h2>{text}</h2>");
                    break;
                case ParagraphType.Quote:
                    content.AppendLine($"  <blockquote>{text}</blockquote>");
                    break;
                case ParagraphType.Code:
                    content.AppendLine($"  <pre><code>{text}</code></pre>");
                    break;
                default:
                    content.AppendLine($"  <p>{text}</p>");
                    break;
            }
        }

        content.AppendLine("</body>");
        content.AppendLine("</html>");

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