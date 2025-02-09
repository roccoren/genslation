namespace genslation.Models;

public class EpubDocument
{
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public List<EpubChapter> Chapters { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
    public string FilePath { get; set; } = string.Empty;
    public byte[]? CoverImage { get; set; }
    public List<EpubResource> Resources { get; set; } = new();
    public List<EpubNavigationItem> TableOfContents { get; set; } = new();
}

public class EpubChapter
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<EpubParagraph> Paragraphs { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
    public string OriginalPath { get; set; } = string.Empty;
    public Dictionary<string, string> StyleAttributes { get; set; } = new();
}

public class EpubParagraph
{
    public string Id { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string TranslatedContent { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public ParagraphType Type { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public string OriginalHtml { get; set; } = string.Empty;
    public List<EpubTextStyle> Styles { get; set; } = new();
}

public class EpubResource
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public ResourceType Type { get; set; }
}

public class EpubTextStyle
{
    public int StartIndex { get; set; }
    public int Length { get; set; }
    public string Style { get; set; } = string.Empty;
    public Dictionary<string, string> Attributes { get; set; } = new();
}

public class EpubNavigationItem
{
    public string Title { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public List<EpubNavigationItem> Children { get; set; } = new();
}

public enum ParagraphType
{
    Text,
    Heading1,
    Heading2,
    Heading3,
    Heading4,
    Heading5,
    Heading6,
    Quote,
    Code,
    List,
    Table,
    Image,
    Other
}

public enum ResourceType
{
    Image,
    Font,
    Stylesheet,
    Other
}