﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Serilog;
using genslation.Configuration;
using genslation.Interfaces;
using genslation.Services;
using genslation.Models;

// Force auto-flush of console output
Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

// Write to both console and file for debugging
File.WriteAllText("debug.log", "Program starting...\n");
Console.WriteLine("Program starting...");
Console.Error.WriteLine("Program starting (stderr)...");

try
{
    File.AppendAllText("debug.log", "Checking for appsettings.json...\n");
    Console.WriteLine("Checking for appsettings.json...");
    // Check for appsettings.json and copy from sample if needed
    if (!File.Exists("appsettings.json") && File.Exists("appsettings.sample.json"))
    {
        File.Copy("appsettings.sample.json", "appsettings.json");
        // Suppress console output for appsettings creation
    }

    // Initialize essential services first
    var loggerFactory = new LoggerFactory();
    var configService = new ConfigurationService(
        loggerFactory.CreateLogger<ConfigurationService>());
    var settings = configService.Settings;

    // Handle help and validation before setting up full services
    if (args.Length == 0 || (args.Length == 1 && (args[0] == "--help" || args[0] == "-h")))
    {
        Console.WriteLine(@"genslation - A tool for translating EPUB files using AI

Usage:
    genslation <input-epub> <output-epub> <source-lang> [target-lang]

Arguments:
    input-epub   Path to the input EPUB file
    output-epub  Path where the translated EPUB will be saved
    source-lang  Source language code (e.g., en, ja, ko)
    target-lang  Target language code (default: zh)

Options:
    -h, --help   Display this help message

Examples:
    genslation input.epub output.epub en zh     # Translate from English to Chinese
    genslation book.epub translated.epub ja zh   # Translate from Japanese to Chinese
    genslation novel.epub output.epub ko        # Translate from Korean to Chinese

Configuration Setup:
    The program requires appsettings.json for configuration. If not present, it will be
    automatically created from appsettings.sample.json on first run.

    Note: Since appsettings.json may contain sensitive data (API keys, etc.),
    it is excluded from source control.

Current Settings (from appsettings.json):
    Translation Provider Settings:
        - Provider: " + settings.TranslationProvider.Provider + @"
        - Model/Deployment: " + (settings.TranslationProvider.Provider == "AzureOpenAI" ? settings.TranslationProvider.AzureOpenAIDeployment : settings.TranslationProvider.OpenAIModel) + @"
        - MaxTokensPerRequest: " + settings.TranslationProvider.MaxTokensPerRequest + @"
        - Temperature: " + settings.TranslationProvider.Temperature + @"
        - TopP: " + settings.TranslationProvider.TopP + @"
        - MaxRetries: " + settings.TranslationProvider.MaxRetries + @"
        - RetryDelay: " + settings.TranslationProvider.RetryDelayMilliseconds + "ms" + @"

    EPUB Processing Settings:
        - PreserveOriginalFormatting: " + settings.Epub.PreserveOriginalFormatting + @"
        - IncludeTranslationMetadata: " + settings.Epub.IncludeTranslationMetadata + @"
        - ValidateOutput: " + settings.Epub.ValidateOutput + @"
        - OutputDirectory: '" + settings.Epub.OutputDirectory + @"'

    Translation Memory:
        - Enabled: " + settings.TranslationMemory.Enabled + @"
        - MinimumSimilarity: " + settings.TranslationMemory.MinimumSimilarity + @"
        - MaxResults: " + settings.TranslationMemory.MaxResults + @"
        - RetentionDays: " + settings.TranslationMemory.RetentionDays + @"

    Logging:
        - Console and file logging enabled
        - Log directory: '" + settings.Logging.LogDirectory + @"'
        - Default log level: " + settings.Logging.LogLevel + @"

For detailed configuration, modify appsettings.json in the program directory.");
        return 0;
    }

    if (args.Length < 3)
    {
        // Suppress error prompts for insufficient arguments
        return 1;
    }

    // Set up full service collection
    var services = new ServiceCollection();

    // Ensure logs directory exists
    var logsDir = Path.GetDirectoryName("logs/genslation-.log");
    if (!string.IsNullOrEmpty(logsDir))
    {
        Directory.CreateDirectory(logsDir);
    }

    // Configure Serilog
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Console()
        .WriteTo.File("logs/genslation-.log", rollingInterval: RollingInterval.Day)
        .CreateLogger();

    services.AddLogging(builder =>
    {
        builder.ClearProviders();
        builder.AddSerilog(Log.Logger);
    });

    // Add configuration and settings
    services.AddSingleton(configService);
    services.AddSingleton(settings);

    // Configure Semantic Kernel
    var kernelBuilder = Kernel.CreateBuilder();

    if (settings.TranslationProvider.Provider == "AzureOpenAI")
    {
        kernelBuilder.AddAzureOpenAIChatCompletion(
            settings.TranslationProvider.AzureOpenAIDeployment,
            settings.TranslationProvider.AzureOpenAIEndpoint,
            settings.TranslationProvider.AzureOpenAIApiKey
        );
    }
    else
    {
        kernelBuilder.AddOpenAIChatCompletion(
            settings.TranslationProvider.OpenAIModel,
            settings.TranslationProvider.OpenAIApiKey
        );
    }

    var kernel = kernelBuilder.Build();
    services.AddSingleton(kernel);

    // Add service implementations
    services.AddSingleton<ITranslationMemoryService, TranslationMemoryService>();
    services.AddSingleton<IEpubProcessor, EpubProcessor>();
    services.AddSingleton<ITranslationProvider>(sp =>
    {
        var logFactory = sp.GetRequiredService<ILoggerFactory>();
        var k = sp.GetRequiredService<Kernel>();
        var config = sp.GetRequiredService<AppSettings>();

        if (config.TranslationProvider.Provider == "AzureOpenAI")
        {
            return new AzureOpenAITranslationProvider(
                k,
                config.TranslationProvider.AzureOpenAIEndpoint,
                config.TranslationProvider.AzureOpenAIDeployment,
                config.TranslationProvider.AzureOpenAIApiKey,
                logFactory.CreateLogger<AzureOpenAITranslationProvider>(),
                new TranslationOptions
                {
                    MaxTokensPerRequest = config.TranslationProvider.MaxTokensPerRequest,
                    MaxRetries = config.TranslationProvider.MaxRetries,
                    RetryDelay = TimeSpan.FromMilliseconds(config.TranslationProvider.RetryDelayMilliseconds),
                    EnableTranslationMemory = false,
                    Temperature = config.TranslationProvider.Temperature,
                    TopP = config.TranslationProvider.TopP
                });
        }
        else
        {
            return new OpenAITranslationProvider(
                k,
                config.TranslationProvider.OpenAIApiKey,
                config.TranslationProvider.OpenAIModel,
                logFactory.CreateLogger<OpenAITranslationProvider>(),
                new TranslationOptions
                {
                    MaxTokensPerRequest = config.TranslationProvider.MaxTokensPerRequest,
                    MaxRetries = config.TranslationProvider.MaxRetries,
                    RetryDelay = TimeSpan.FromMilliseconds(config.TranslationProvider.RetryDelayMilliseconds),
                    EnableTranslationMemory = false,
                    Temperature = config.TranslationProvider.Temperature,
                    TopP = config.TranslationProvider.TopP
                });
        }
    });

    // Register default TranslationOptions
    services.AddSingleton(new TranslationOptions
    {
        MaxTokensPerRequest = settings.TranslationProvider.MaxTokensPerRequest,
        MaxRetries = settings.TranslationProvider.MaxRetries,
        RetryDelay = TimeSpan.FromMilliseconds(settings.TranslationProvider.RetryDelayMilliseconds),
        EnableTranslationMemory = false,
        PreserveFormatting = settings.Epub.PreserveOriginalFormatting,
        Temperature = settings.TranslationProvider.Temperature,
        TopP = settings.TranslationProvider.TopP
    });

    services.AddSingleton<TranslationService>();

    var serviceProvider = services.BuildServiceProvider();

    // Process the translation
    var translationService = serviceProvider.GetRequiredService<TranslationService>();
    var epubProcessor = serviceProvider.GetRequiredService<IEpubProcessor>();
    var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

    var inputFile = args[0];
    var outputFile = args[1];
    var sourceLang = args[2];
    var targetLang = args.Length > 3 ? args[3] : "zh";

    // Ensure output directory exists
    var outputDir = Path.GetDirectoryName(outputFile);
    if (!string.IsNullOrEmpty(outputDir))
    {
        Directory.CreateDirectory(outputDir);
    }

    // Load the ePub
    logger.LogInformation("Loading ePub: {InputFile}", inputFile);
    var document = await epubProcessor.LoadAsync(inputFile);

    // Track total translation time
    var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

    // Translate the document
    logger.LogInformation("Translating from {SourceLang} to {TargetLang}", sourceLang, targetLang);
    var translatedDocument = await translationService.TranslateDocumentAsync(
        document,
        new TranslationOptions
        {
            SourceLanguage = sourceLang,
            TargetLanguage = targetLang,
            EnableTranslationMemory = false,
            MaxTokensPerRequest = settings.TranslationProvider.MaxTokensPerRequest,
            Temperature = settings.TranslationProvider.Temperature,
            TopP = settings.TranslationProvider.TopP
        });

    totalStopwatch.Stop();

    if (translatedDocument.TranslationMetrics == null)
    {
        translatedDocument.TranslationMetrics = new TranslationMetrics();
    }
    translatedDocument.TranslationMetrics.ProcessingTime = totalStopwatch.Elapsed;

    // Save the translated ePub
    logger.LogInformation("Saving translated ePub: {OutputFile}", outputFile);
    var success = await epubProcessor.SaveTranslatedEpubAsync(
        translatedDocument,
        outputFile,
        new TranslationOptions
        {
            PreserveFormatting = settings.Epub.PreserveOriginalFormatting,
            EnableTranslationMemory = false
        });

    if (success)
    {
        // Display detailed performance metrics
        if (translatedDocument.TranslationMetrics != null)
        {
            var metrics = translatedDocument.TranslationMetrics;
            Console.WriteLine("\nTranslation Performance Metrics");
            Console.WriteLine("=============================");
            Console.WriteLine("Time and Processing");
            Console.WriteLine($"  Total Time: {metrics.ProcessingTime.TotalSeconds:N2} seconds");

            if (metrics.ChapterTokenCounts.Any())
            {
                Console.WriteLine("\nToken Distribution");
                foreach (var (chunk, tokens) in metrics.ChapterTokenCounts)
                {
                    Console.WriteLine($"  {chunk}: {tokens:N0} tokens");
                }
            }

            if (metrics.Cost > 0)
            {
                Console.WriteLine("\nCost Information");
                Console.WriteLine($"  Total Cost: ${metrics.Cost:N4}");
            }
            
            Console.WriteLine("=============================\n");
            // Clear token usage summary after displaying metrics
            translatedDocument.TranslationMetrics = null;
        }

        logger.LogInformation("Translation completed successfully");
        return 0;
    }
    else
    {
        logger.LogError("Failed to save translated ePub");
        return 1;
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "An unhandled exception occurred");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
