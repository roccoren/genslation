namespace genslation.Models;

public class EpubDocument
{
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public List<EpubChapter> Chapters { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
    public string FilePath { get; set; } = string.Empty;
}

public class EpubChapter
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<EpubParagraph> Paragraphs { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
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
}

public enum ParagraphType
{
    Text,
    Header,
    Quote,
    Code,
    List,
    Table,
    Image,
    Other
}