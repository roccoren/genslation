namespace genslation.Configuration;

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public class ConfigurationService
{
    private readonly ILogger<ConfigurationService> _logger;
    private readonly string _configPath;
    private AppSettings _settings;

    public ConfigurationService(ILogger<ConfigurationService> logger, string configPath = "appsettings.json")
    {
        _logger = logger;
        _configPath = configPath;
        _settings = LoadConfiguration();
    }

    public AppSettings Settings => _settings;

    private AppSettings LoadConfiguration()
    {
        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile(_configPath, optional: false, reloadOnChange: true)
                .Build();

            var settings = new AppSettings();
            configuration.Bind(settings);

            ValidateConfiguration(settings);
            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load configuration from {ConfigPath}", _configPath);
            throw;
        }
    }

    private void ValidateConfiguration(AppSettings settings)
    {
        var errors = new List<string>();

        // Validate translation provider settings
        if (settings.TranslationProvider.Provider != "OpenAI" && 
            settings.TranslationProvider.Provider != "AzureOpenAI")
        {
            errors.Add("Invalid translation provider. Must be either 'OpenAI' or 'AzureOpenAI'.");
        }

        if (settings.TranslationProvider.Provider == "OpenAI" && 
            string.IsNullOrEmpty(settings.TranslationProvider.OpenAIApiKey))
        {
            errors.Add("OpenAI API key is required when using OpenAI provider.");
        }

        if (settings.TranslationProvider.Provider == "AzureOpenAI")
        {
            if (string.IsNullOrEmpty(settings.TranslationProvider.AzureOpenAIEndpoint))
                errors.Add("Azure OpenAI endpoint is required when using Azure OpenAI provider.");
            if (string.IsNullOrEmpty(settings.TranslationProvider.AzureOpenAIDeployment))
                errors.Add("Azure OpenAI deployment name is required when using Azure OpenAI provider.");
            if (string.IsNullOrEmpty(settings.TranslationProvider.AzureOpenAIApiKey))
                errors.Add("Azure OpenAI API key is required when using Azure OpenAI provider.");
        }

        // Validate translation memory settings
        if (settings.TranslationMemory.MinimumSimilarity < 0 || settings.TranslationMemory.MinimumSimilarity > 1)
        {
            errors.Add("Translation memory minimum similarity must be between 0 and 1.");
        }

        if (settings.TranslationMemory.MaxResults <= 0)
        {
            errors.Add("Translation memory max results must be greater than 0.");
        }

        if (settings.TranslationMemory.RetentionDays <= 0)
        {
            errors.Add("Translation memory retention days must be greater than 0.");
        }

        // Validate paths
        if (settings.TranslationMemory.Enabled && 
            !IsValidPath(settings.TranslationMemory.StorageDirectory))
        {
            errors.Add($"Invalid translation memory storage directory: {settings.TranslationMemory.StorageDirectory}");
        }

        if (!IsValidPath(settings.Epub.OutputDirectory))
        {
            errors.Add($"Invalid ePub output directory: {settings.Epub.OutputDirectory}");
        }

        if (settings.Logging.FileLogging && !IsValidPath(settings.Logging.LogDirectory))
        {
            errors.Add($"Invalid log directory: {settings.Logging.LogDirectory}");
        }

        if (errors.Any())
        {
            var errorMessage = $"Configuration validation failed:\n{string.Join("\n", errors)}";
            _logger.LogError(errorMessage);
            throw new InvalidOperationException(errorMessage);
        }
    }

    private bool IsValidPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory!);
            }
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invalid path: {Path}", path);
            return false;
        }
    }

    public void SaveConfiguration()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save configuration to {ConfigPath}", _configPath);
            throw;
        }
    }

    public void UpdateConfiguration(Action<AppSettings> updateAction)
    {
        updateAction(_settings);
        ValidateConfiguration(_settings);
        SaveConfiguration();
    }
}