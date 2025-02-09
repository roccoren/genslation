# Genslation

A C# .NET tool for translating ePub books while preserving their original structure and formatting, using Microsoft's Semantic Kernel for high-quality translations.

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

- .NET 8.0 SDK
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

## Configuration

Configure the application by editing `appsettings.json`:

```json
{
  "TranslationProvider": {
    "Provider": "OpenAI",  // or "AzureOpenAI"
    "OpenAIApiKey": "your-api-key",
    "OpenAIModel": "gpt-4-turbo-preview",
    "AzureOpenAIEndpoint": "",
    "AzureOpenAIDeployment": "",
    "AzureOpenAIApiKey": ""
  }
}
```

## Usage

Basic usage:
```bash
dotnet run <input-epub> <output-epub> <source-lang> <target-lang>
```

Example:
```bash
dotnet run book.epub book-zh.epub en zh
```

## Features in Detail

### Translation Memory

The translation memory system helps maintain consistency across translations and improves efficiency by reusing previously translated content. Configure its behavior in `appsettings.json`:

```json
{
  "TranslationMemory": {
    "Enabled": true,
    "MinimumSimilarity": 0.9,
    "MaxResults": 5,
    "RetentionDays": 90
  }
}
```

### Quality Control

Quality checks are performed during translation:
- Language detection and verification
- Formatting consistency checks
- Technical terminology preservation
- Context-aware translation

### Error Handling

The application includes robust error handling:
- Automatic retry for API failures
- Fallback mechanisms
- Detailed error logging
- Progress preservation

## Contributing

1. Fork the repository
2. Create a feature branch
3. Commit your changes
4. Push to the branch
5. Create a Pull Request

## License

MIT License. See [LICENSE](LICENSE) for details.