namespace genslation.Services;

using System.Diagnostics;
using System.Text.Json;
using genslation.Models;
using genslation.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

public class OpenAITranslationProvider : BaseTranslationProvider
{
    public override async Task<TranslationResult> TranslateChunkAsync(
        string chunk,
        string sourceLanguage,
        string targetLanguage,
        TranslationOptions options,
        CancellationToken cancellationToken = default)
    {
        var prompt = CreateTranslationPrompt(chunk, sourceLanguage, targetLanguage, options);
        var promptExecutionSettings = new OpenAIPromptExecutionSettings
        {
            MaxTokens = options.MaxTokensPerRequest,
            Temperature = options.Temperature ?? 0.3,
            TopP = options.TopP ?? 0.95,
            ChatSystemPrompt = "You are a professional translator specialized in technical content."
        };

        var function = _kernel.CreateFunctionFromPrompt(prompt, promptExecutionSettings);
        var functionResult = await _kernel.InvokeAsync(function, new KernelArguments(), cancellationToken);

        if (functionResult.Metadata.ContainsKey("Error"))
        {
            throw new Exception(functionResult.Metadata["Error"]?.ToString() ?? "Unknown error");
        }

        return new TranslationResult
        {
            TranslatedContent = functionResult.GetValue<string>() ?? string.Empty,
            Success = true
        };
    }
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

}