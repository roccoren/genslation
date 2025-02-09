namespace genslation.Services;

using System.Diagnostics;
using genslation.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

public class OpenAITranslationProvider : BaseTranslationProvider
{
    private readonly Kernel _kernel;
    private readonly string _apiKey;
    private readonly string _modelName;

    public OpenAITranslationProvider(
        Kernel kernel,
        string apiKey,
        string modelName,
        ILogger<OpenAITranslationProvider> logger,
        TranslationOptions defaultOptions)
        : base(logger, defaultOptions)
    {
        _kernel = kernel;
        _apiKey = apiKey;
        _modelName = modelName;
    }

    public override string Name => "OpenAI";

    public override async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        TranslationOptions options,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new TranslationResult
            {
                OriginalContent = text,
                Metrics = new TranslationMetrics
                {
                    Provider = Name,
                    TokenCount = await EstimateTokenCount(text)
                }
            };

            if (string.IsNullOrWhiteSpace(text))
            {
                return CreateErrorResult(text, "Empty text provided");
            }

            var chunks = await SplitIntoChunks(text, options.MaxTokensPerRequest);
            var translatedChunks = new List<string>();

            foreach (var chunk in chunks)
            {
                var prompt = CreateTranslationPrompt(chunk, sourceLanguage, targetLanguage, options);
                
                try
                {
                    var promptExecutionSettings = new OpenAIPromptExecutionSettings 
                    {
                        MaxTokens = 2000,
                        Temperature = 0.3,
                        TopP = 0.95,
                        ChatSystemPrompt = "You are a professional translator specialized in technical content."
                    };

                    var function = _kernel.CreateFunctionFromPrompt(prompt, promptExecutionSettings);
                    var functionResult = await _kernel.InvokeAsync(function, new KernelArguments());

                    if (functionResult.Metadata.ContainsKey("Error"))
                    {
                        _logger.LogError("Translation error for chunk: {Error}", functionResult.Metadata["Error"]);
                        return CreateErrorResult(text, $"Translation failed: {functionResult.Metadata["Error"]}");
                    }

                    translatedChunks.Add(functionResult.GetValue<string>() ?? string.Empty);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to translate chunk");
                    return CreateErrorResult(text, $"Translation failed: {ex.Message}");
                }
            }

            stopwatch.Stop();
            result.TranslatedContent = string.Join("", translatedChunks);
            result.Success = true;
            result.Metrics.ProcessingTime = stopwatch.Elapsed;

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Translation failed");
            return CreateErrorResult(text, $"Translation failed: {ex.Message}");
        }
    }

    public override async Task<bool> ValidateConfigurationAsync()
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            _logger.LogError("OpenAI API key is not configured");
            return false;
        }

        try
        {
            // Attempt a simple translation to validate configuration
            var result = await TranslateAsync(
                "test",
                "en",
                "zh",
                _defaultOptions);

            return result.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Configuration validation failed");
            return false;
        }
    }

    private string CreateTranslationPrompt(
        string text,
        string sourceLanguage,
        string targetLanguage,
        TranslationOptions options)
    {
        return @$"You are a professional translator with expertise in {sourceLanguage} and {targetLanguage}.
Please translate the following text from {sourceLanguage} to {targetLanguage}.
Preserve all formatting, line breaks, and special characters.
If there are technical terms or idioms, ensure they are translated appropriately for the target culture.

Text to translate:
{text}

Requirements:
- Maintain the original meaning and tone
- Preserve formatting and structure
- Ensure natural flow in the target language
- Keep technical terms accurate
- Maintain any markdown or HTML formatting if present

Translation:";
    }
}