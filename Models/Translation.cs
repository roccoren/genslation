namespace genslation.Models;

public class TranslationOptions
{
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "zh";
    public int MaxTokensPerRequest { get; set; } = 2000;
    public bool UseContextualHints { get; set; } = true;
    public bool EnableTranslationMemory { get; set; } = true;
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);
    public bool PreserveFormatting { get; set; } = true;
    public QualityCheckLevel QualityCheckLevel { get; set; } = QualityCheckLevel.Standard;
    public Dictionary<string, string> CustomTerminology { get; set; } = new();
    public double? Temperature { get; set; } = 0.3;
    public double? TopP { get; set; } = 0.95;
}

public class TranslationResult
{
    public string OriginalContent { get; set; } = string.Empty;
    public string TranslatedContent { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
    public double QualityScore { get; set; }
    public List<QualityIssue> QualityIssues { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
    public TranslationMetrics Metrics { get; set; } = new();
}

public class TranslationMetrics
{
    public int TokenCount { get; set; }
    public int CharacterCount { get; set; }
    public TimeSpan ProcessingTime { get; set; }
    public int RetryCount { get; set; }
    public string Provider { get; set; } = string.Empty;
    public double Cost { get; set; }
}

public class QualityIssue
{
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Suggestion { get; set; } = string.Empty;
    public int Severity { get; set; }
    public TextSpan Location { get; set; } = new();
}

public class TextSpan
{
    public int Start { get; set; }
    public int Length { get; set; }
    public string Context { get; set; } = string.Empty;
}

public class TranslationMemoryEntry
{
    public string SourceText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsed { get; set; }
    public int UseCount { get; set; }
    public double QualityScore { get; set; }
    public string Context { get; set; } = string.Empty;
}

public enum QualityCheckLevel
{
    Basic,
    Standard,
    Strict
}