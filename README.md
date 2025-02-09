# Genslation

A C# .NET tool for translating ePub books while preserving their original structure and formatting, leveraging AI language models for high-quality translations.

## Features

- Provider-agnostic translation service supporting both OpenAI and Azure OpenAI
- Translation memory system for consistency and efficiency
- Preserves ePub structure and formatting
- Contextual translation using surrounding paragraphs
- Batch processing capabilities
- Error handling and recovery
- Configurable quality checks
- Progress tracking and detailed logging

## Requirements

- .NET 9.0 SDK
- OpenAI API key or Azure OpenAI subscription
- Sufficient disk space for translation memory

## Installation

1. Clone the repository:
```bash
git clone https://github.com/yourusername/genslation.git
cd genslation
```

2. Build the project:
```bash
dotnet build
```

3. Copy and configure settings:
```bash
cp appsettings.sample.json appsettings.json
```

## Configuration

The application is configured through `appsettings.json` with the following sections:

### Translation Provider

```json
{
  "TranslationProvider": {
    "Provider": "OpenAI",  // or "AzureOpenAI"
    "OpenAIApiKey": "your-openai-api-key-here",
    "OpenAIModel": "gpt-4-turbo-preview",
    "AzureOpenAIEndpoint": "https://your-resource.openai.azure.com/",
    "AzureOpenAIDeployment": "your-deployment-name",
    "AzureOpenAIApiKey": "your-azure-openai-key-here",
    "MaxTokensPerRequest": 2000,
    "MaxRetries": 3,
    "RetryDelayMilliseconds": 1000,
    "Temperature": 0.3,
    "TopP": 0.95
  }
}
```

### Translation Memory

```json
{
  "TranslationMemory": {
    "Enabled": true,
    "StorageDirectory": "translation_memory",
    "MinimumSimilarity": 0.9,
    "MaxResults": 5,
    "RetentionDays": 90,
    "AutoOptimize": true
  }
}
```

### EPub Settings

```json
{
  "Epub": {
    "OutputDirectory": "output",
    "PreserveOriginalFormatting": true,
    "IncludeTranslationMetadata": true,
    "ValidateOutput": true
  }
}
```

### Logging

```json
{
  "Logging": {
    "LogLevel": "Information",
    "LogDirectory": "logs",
    "ConsoleLogging": true,
    "FileLogging": true,
    "FilenamePattern": "genslation-{Date}.log",
    "IncludeScopes": true
  }
}
```

## Usage

Basic usage:
```bash
genslation <input-epub> <output-epub> <source-lang> <target-lang>
```

Example:
```bash
genslation book.epub book-zh.epub en zh
```

Parameters:
- `input-epub`: Path to the source ePub file
- `output-epub`: Path where the translated ePub will be saved
- `source-lang`: Source language code (e.g., 'en', 'ja', 'ko')
- `target-lang`: Target language code (defaults to 'zh' if not specified)

## Features in Detail

### Translation Provider Options

- `MaxTokensPerRequest`: Maximum tokens per API request
- `MaxRetries`: Number of retries for failed API calls
- `RetryDelayMilliseconds`: Delay between retries
- `Temperature`: Controls randomness in translation (0.0-1.0)
- `TopP`: Controls diversity in translation (0.0-1.0)

### Translation Memory

The translation memory system helps maintain consistency across translations by:
- Storing and reusing previous translations
- Matching similar content based on configurable similarity threshold
- Automatic optimization of the translation memory database
- Configurable retention period for translations

### EPub Processing

- Preserves original ePub structure and formatting
- Optionally includes translation metadata
- Validates output ePub files
- Customizable output directory

### Logging

- Comprehensive logging system
- Console and file logging support
- Configurable log levels
- Daily log rotation
- Detailed error tracking

## Contributing

1. Fork the repository
2. Create a feature branch
3. Commit your changes
4. Push to the branch
5. Create a Pull Request

## License

MIT License. See [LICENSE](LICENSE) for details.