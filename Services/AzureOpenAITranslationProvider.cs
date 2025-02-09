namespace genslation.Services;

using System.Diagnostics;
using System.Text.Json;
using genslation.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

public class AzureOpenAITranslationProvider : BaseTranslationProvider
{
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
            var estimatedTokens = await EstimateTokenCount(text);
            var requestId = Guid.NewGuid().ToString("N");
            
            _logger.LogInformation(
                "Translation Request [{RequestId}] Started at {Timestamp}\nEndpoint: {Endpoint}\nDeployment: {Model}\nSource Language: {SourceLang}\nTarget Language: {TargetLang}\nEstimated Tokens: {Tokens}",
                requestId,
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                _endpoint,
                _deploymentName,
                sourceLanguage,
                targetLanguage,
                estimatedTokens);

            var result = new TranslationResult
            {
                OriginalContent = text,
                Metrics = new TranslationMetrics
                {
                    Provider = Name,
                    TokenCount = estimatedTokens
                }
            };

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogWarning("[{RequestId}] Empty text provided for translation", requestId);
                return CreateErrorResult(text, "Empty text provided");
            }

            var chunks = await SplitIntoChunks(text, options.MaxTokensPerRequest);
            _logger.LogInformation("[{RequestId}] Split text into {ChunkCount} chunks", requestId, chunks.Count);
            
            var translatedChunks = new List<string>();
            var chunkIndex = 0;

            foreach (var chunk in chunks)
            {
                chunkIndex++;
                var chunkStopwatch = Stopwatch.StartNew();
                var prompt = CreateTranslationPrompt(chunk, sourceLanguage, targetLanguage, options);
                
                try
                {
                    _logger.LogInformation(
                        "Request [{RequestId}] Chunk {Index}/{Total} at {Timestamp}\n=== Source Text ===\n{Text}\n=== Prompt ===\n{Prompt}",
                        requestId,
                        chunkIndex,
                        chunks.Count,
                        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        chunk,
                        prompt);

                    var promptExecutionSettings = new OpenAIPromptExecutionSettings
                    {
                        MaxTokens = options.MaxTokensPerRequest,
                        Temperature = options.Temperature ?? 0.3,
                        TopP = options.TopP ?? 0.95,
                        ChatSystemPrompt = "You are a professional translator specialized in technical content."
                    };

                    _logger.LogDebug(
                        "[{RequestId}] API Settings: Model={Model}, MaxTokens={MaxTokens}, Temperature={Temperature}, TopP={TopP}",
                        requestId,
                        _deploymentName,
                        promptExecutionSettings.MaxTokens,
                        promptExecutionSettings.Temperature,
                        promptExecutionSettings.TopP);

                    var function = _kernel.CreateFunctionFromPrompt(prompt, promptExecutionSettings);
                    var functionResult = await _kernel.InvokeAsync(function, new KernelArguments());

                    if (functionResult.Metadata.ContainsKey("Error"))
                    {
                        var error = functionResult.Metadata["Error"];
                        _logger.LogError(
                            "Response [{RequestId}] Error at {Timestamp}\nChunk {Index}: {Error}\n=== Failed Text ===\n{Text}",
                            requestId,
                            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                            chunkIndex,
                            error,
                            chunk);
                        return CreateErrorResult(text, $"Translation failed: {error}");
                    }

                    var translatedText = functionResult.GetValue<string>() ?? string.Empty;
                    translatedChunks.Add(translatedText);

                    chunkStopwatch.Stop();
                    _logger.LogInformation(
                        "Response [{RequestId}] Success at {Timestamp}\nChunk {Index}/{Total} completed in {Duration}ms\n=== Translated Text ===\n{Translation}",
                        requestId,
                        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        chunkIndex,
                        chunks.Count,
                        chunkStopwatch.ElapsedMilliseconds,
                        translatedText);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        "Response [{RequestId}] Error at {Timestamp}\nChunk {Index}/{Total}: {Error}\n=== Stack Trace ===\n{StackTrace}\n=== Failed Text ===\n{Text}",
                        requestId,
                        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        chunkIndex,
                        chunks.Count,
                        ex.Message,
                        ex.StackTrace,
                        chunk);
                    return CreateErrorResult(text, $"Translation failed: {ex.Message}");
                }
            }

            stopwatch.Stop();
            result.TranslatedContent = string.Join("", translatedChunks);
            result.Success = true;
            result.Metrics.ProcessingTime = stopwatch.Elapsed;

            _logger.LogInformation(
                "Translation [{RequestId}] Completed at {Timestamp}\nTotal Chunks: {Chunks}\nTotal Time: {Duration}ms\nAverage Time Per Chunk: {AverageTime}ms",
                requestId,
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                chunks.Count,
                stopwatch.ElapsedMilliseconds,
                stopwatch.ElapsedMilliseconds / chunks.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Translation failed at {Timestamp}\nError: {Message}\nStack Trace: {StackTrace}",
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                ex.Message,
                ex.StackTrace);
            return CreateErrorResult(text, $"Translation failed: {ex.Message}");
        }
    }

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