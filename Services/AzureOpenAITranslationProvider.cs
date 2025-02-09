namespace genslation.Services;

using System.Diagnostics;
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
            
            _logger.LogInformation(
                "Starting translation: Source={SourceLang}, Target={TargetLang}, Tokens={Tokens}, Model={Model}",
                sourceLanguage,
                targetLanguage,
                estimatedTokens,
                _deploymentName);

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
                _logger.LogWarning("Empty text provided for translation");
                return CreateErrorResult(text, "Empty text provided");
            }

            var chunks = await SplitIntoChunks(text, options.MaxTokensPerRequest);
            _logger.LogInformation("Split text into {ChunkCount} chunks", chunks.Count);
            
            var translatedChunks = new List<string>();
            var chunkIndex = 0;

            foreach (var chunk in chunks)
            {
                chunkIndex++;
                var chunkStopwatch = Stopwatch.StartNew();
                var prompt = CreateTranslationPrompt(chunk, sourceLanguage, targetLanguage, options);
                
                try
                {
                    _logger.LogTrace(
                        "Translating chunk {Index}/{Total}: Length={Length}, Text={Text}",
                        chunkIndex,
                        chunks.Count,
                        chunk.Length,
                        chunk.Length > 50 ? chunk.Substring(0, 50) + "..." : chunk);

                    var promptExecutionSettings = new OpenAIPromptExecutionSettings
                    {
                        MaxTokens = 2000,
                        Temperature = options.Temperature ?? 0.3,
                        TopP = options.TopP ?? 0.95,
                        ChatSystemPrompt = "You are a professional translator specialized in technical content."
                    };

                    _logger.LogDebug(
                        "API Request: Model={Model}, MaxTokens={MaxTokens}, Temperature={Temperature}, TopP={TopP}",
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
                            "Translation error for chunk {Index}: {Error}, Text={Text}",
                            chunkIndex,
                            error,
                            chunk);
                        return CreateErrorResult(text, $"Translation failed: {error}");
                    }

                    var translatedText = functionResult.GetValue<string>() ?? string.Empty;
                    translatedChunks.Add(translatedText);

                    chunkStopwatch.Stop();
                    _logger.LogInformation(
                        "Chunk {Index}/{Total} translated successfully in {Duration}ms",
                        chunkIndex,
                        chunks.Count,
                        chunkStopwatch.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to translate chunk {Index}/{Total}: {Error}, Text={Text}",
                        chunkIndex,
                        chunks.Count,
                        ex.Message,
                        chunk);
                    return CreateErrorResult(text, $"Translation failed: {ex.Message}");
                }
            }

            stopwatch.Stop();
            result.TranslatedContent = string.Join("", translatedChunks);
            result.Success = true;
            result.Metrics.ProcessingTime = stopwatch.Elapsed;

            _logger.LogInformation(
                "Translation completed: Chunks={Chunks}, TotalTime={Duration}ms, AverageChunkTime={AverageTime}ms",
                chunks.Count,
                stopwatch.ElapsedMilliseconds,
                stopwatch.ElapsedMilliseconds / chunks.Count);

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
        if (string.IsNullOrEmpty(_apiKey) || 
            string.IsNullOrEmpty(_endpoint) || 
            string.IsNullOrEmpty(_deploymentName))
        {
            _logger.LogError("Azure OpenAI configuration is incomplete");
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