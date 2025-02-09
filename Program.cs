﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Serilog;
using genslation.Configuration;
using genslation.Interfaces;
using genslation.Services;
using genslation.Models;

var services = new ServiceCollection();

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

// Add configuration
var configService = new ConfigurationService(
    new LoggerFactory().CreateLogger<ConfigurationService>());
services.AddSingleton(configService);
services.AddSingleton(configService.Settings);

// Configure Semantic Kernel
var kernelBuilder = Kernel.CreateBuilder();

if (configService.Settings.TranslationProvider.Provider == "AzureOpenAI")
{
    kernelBuilder.AddAzureOpenAIChatCompletion(
        configService.Settings.TranslationProvider.AzureOpenAIDeployment,
        configService.Settings.TranslationProvider.AzureOpenAIEndpoint,
        configService.Settings.TranslationProvider.AzureOpenAIApiKey
    );
}
else
{
    kernelBuilder.AddOpenAIChatCompletion(
        configService.Settings.TranslationProvider.OpenAIModel,
        configService.Settings.TranslationProvider.OpenAIApiKey
    );
}

var kernel = kernelBuilder.Build();
services.AddSingleton(kernel);

// Add services
services.AddSingleton<ITranslationMemoryService, TranslationMemoryService>();
services.AddSingleton<IEpubProcessor, EpubProcessor>();
services.AddSingleton<ITranslationProvider>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var k = sp.GetRequiredService<Kernel>();
    var settings = sp.GetRequiredService<AppSettings>();

    if (settings.TranslationProvider.Provider == "AzureOpenAI")
    {
        return new AzureOpenAITranslationProvider(
            k,
            settings.TranslationProvider.AzureOpenAIEndpoint,
            settings.TranslationProvider.AzureOpenAIDeployment,
            settings.TranslationProvider.AzureOpenAIApiKey,
            loggerFactory.CreateLogger<AzureOpenAITranslationProvider>(),
            new TranslationOptions
            {
                MaxTokensPerRequest = settings.TranslationProvider.MaxTokensPerRequest,
                MaxRetries = settings.TranslationProvider.MaxRetries,
                RetryDelay = TimeSpan.FromMilliseconds(settings.TranslationProvider.RetryDelayMilliseconds)
            });
    }
    else
    {
        return new OpenAITranslationProvider(
            k,
            settings.TranslationProvider.OpenAIApiKey,
            settings.TranslationProvider.OpenAIModel,
            loggerFactory.CreateLogger<OpenAITranslationProvider>(),
            new TranslationOptions
            {
                MaxTokensPerRequest = settings.TranslationProvider.MaxTokensPerRequest,
                MaxRetries = settings.TranslationProvider.MaxRetries,
                RetryDelay = TimeSpan.FromMilliseconds(settings.TranslationProvider.RetryDelayMilliseconds)
            });
    }
});

// Register default TranslationOptions
services.AddSingleton(new TranslationOptions
{
    MaxTokensPerRequest = configService.Settings.TranslationProvider.MaxTokensPerRequest,
    MaxRetries = configService.Settings.TranslationProvider.MaxRetries,
    RetryDelay = TimeSpan.FromMilliseconds(configService.Settings.TranslationProvider.RetryDelayMilliseconds),
    EnableTranslationMemory = configService.Settings.TranslationMemory.Enabled,
    PreserveFormatting = configService.Settings.Epub.PreserveOriginalFormatting
});

services.AddSingleton<TranslationService>();

var serviceProvider = services.BuildServiceProvider();

try
{
    var translationService = serviceProvider.GetRequiredService<TranslationService>();
    var epubProcessor = serviceProvider.GetRequiredService<IEpubProcessor>();
    var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

    if (args.Length < 3)
    {
        Console.WriteLine("Usage: genslation <input-epub> <output-epub> <source-lang> <target-lang>");
        return 1;
    }

    var inputFile = args[0];
    var outputFile = args[1];
    var sourceLang = args[2];
    var targetLang = args.Length > 3 ? args[3] : "zh";

    // Load the ePub
    logger.LogInformation("Loading ePub: {InputFile}", inputFile);
    var document = await epubProcessor.LoadAsync(inputFile);

    // Translate the document
    logger.LogInformation("Translating from {SourceLang} to {TargetLang}", sourceLang, targetLang);
    var translatedDocument = await translationService.TranslateDocumentAsync(
        document,
        new TranslationOptions 
        { 
            SourceLanguage = sourceLang,
            TargetLanguage = targetLang,
            EnableTranslationMemory = configService.Settings.TranslationMemory.Enabled,
            MaxTokensPerRequest = configService.Settings.TranslationProvider.MaxTokensPerRequest
        });

    // Save the translated ePub
    logger.LogInformation("Saving translated ePub: {OutputFile}", outputFile);
    var success = await epubProcessor.SaveTranslatedEpubAsync(
        translatedDocument,
        outputFile,
        new TranslationOptions
        {
            PreserveFormatting = configService.Settings.Epub.PreserveOriginalFormatting
        });

    if (success)
    {
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
