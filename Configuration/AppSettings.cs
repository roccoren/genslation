namespace genslation.Configuration;

public class AppSettings
{
    public TranslationProviderSettings TranslationProvider { get; set; } = new();
    public TranslationMemorySettings TranslationMemory { get; set; } = new();
    public EpubSettings Epub { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
}

public class TranslationProviderSettings
{
    public string Provider { get; set; } = "OpenAI"; // OpenAI or AzureOpenAI
    public string OpenAIApiKey { get; set; } = string.Empty;
    public string OpenAIModel { get; set; } = "gpt-4-turbo-preview";
    public string AzureOpenAIEndpoint { get; set; } = string.Empty;
    public string AzureOpenAIDeployment { get; set; } = string.Empty;
    public string AzureOpenAIApiKey { get; set; } = string.Empty;
    public int MaxTokensPerRequest { get; set; } = 2000;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 1000;
    public double Temperature { get; set; } = 0.3;
    public double TopP { get; set; } = 0.95;
}

public class TranslationMemorySettings
{
    public bool Enabled { get; set; } = true;
    public string StorageDirectory { get; set; } = "translation_memory";
    public double MinimumSimilarity { get; set; } = 0.9;
    public int MaxResults { get; set; } = 5;
    public int RetentionDays { get; set; } = 90;
    public bool AutoOptimize { get; set; } = true;
}

public class ConcurrencySettings
{
    public Dictionary<int, ConcurrencyLevel> WordCountThresholds { get; set; } = new()
    {
        { 50, new ConcurrencyLevel { Threads = 8, Description = "Very short paragraphs", DelayMs = 100 } },
        { 100, new ConcurrencyLevel { Threads = 6, Description = "Short paragraphs", DelayMs = 150 } },
        { 200, new ConcurrencyLevel { Threads = 4, Description = "Medium paragraphs", DelayMs = 200 } },
        { int.MaxValue, new ConcurrencyLevel { Threads = 2, Description = "Long paragraphs", DelayMs = 250 } }
    };
}

public class ConcurrencyLevel
{
    public int Threads { get; set; }
    public string Description { get; set; } = string.Empty;
    public int DelayMs { get; set; }
}

public class EpubSettings
{
    public string OutputDirectory { get; set; } = "output";
    public bool PreserveOriginalFormatting { get; set; } = true;
    public bool IncludeTranslationMetadata { get; set; } = true;
    public bool ValidateOutput { get; set; } = true;
    public ConcurrencySettings Concurrency { get; set; } = new();
}

public class LoggingSettings
{
    public string LogLevel { get; set; } = "Information";
    public string LogDirectory { get; set; } = "logs";
    public bool ConsoleLogging { get; set; } = true;
    public bool FileLogging { get; set; } = true;
    public string FilenamePattern { get; set; } = "genslation-{Date}.log";
    public bool IncludeScopes { get; set; } = true;
}