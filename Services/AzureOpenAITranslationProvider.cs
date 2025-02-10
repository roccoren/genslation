namespace genslation.Services;

using System.Diagnostics;
using System.Text.Json;
using genslation.Models;
using genslation.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

public class AzureOpenAITranslationProvider : BaseTranslationProvider
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
            TranslatedContent = functionResult.GetValue<string>() as string ?? string.Empty,
            Success = true
        };
    }
    private readonly Kernel _kernel;
    private readonly string _endpoint;
    private readonly string _deploymentName;
    private readonly string _apiKey;

    public AzureOpenAITranslationProvider(
        Kernel kernel,
        string endpoint,
        string deploymentName,
        string apiKey,
        ILogger<AzureOpenAITranslationProvider> logger,
        TranslationOptions defaultOptions)
        : base(logger, defaultOptions)
    {
        _kernel = kernel;
        _endpoint = endpoint;
        _deploymentName = deploymentName;
        _apiKey = apiKey;
    }

    public override string Name => "AzureOpenAI";


    public override async Task<bool> ValidateConfigurationAsync()
    {
        var validationId = Guid.NewGuid().ToString("N");
        _logger.LogInformation(
            "Configuration Validation [{ValidationId}] Started at {Timestamp}\nEndpoint: {Endpoint}\nDeployment: {Model}",
            validationId,
            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            _endpoint,
            _deploymentName);

        if (string.IsNullOrEmpty(_apiKey) || 
            string.IsNullOrEmpty(_endpoint) || 
            string.IsNullOrEmpty(_deploymentName))
        {
            _logger.LogError(
                "[{ValidationId}] Validation Failed: Azure OpenAI configuration is incomplete",
                validationId);
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

            _logger.LogInformation(
                "Configuration Validation [{ValidationId}] Completed at {Timestamp}\nStatus: {Status}",
                validationId,
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                result.Success ? "Success" : "Failed");

            return result.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Configuration Validation [{ValidationId}] Failed at {Timestamp}\nError: {Message}",
                validationId,
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                ex.Message);
            return false;
        }
    }

}