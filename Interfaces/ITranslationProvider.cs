namespace genslation.Interfaces;

using genslation.Models;

public interface ITranslationProvider
{
    string Name { get; }
    
    Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        TranslationOptions options,
        CancellationToken cancellationToken = default);
    
    Task<bool> ValidateConfigurationAsync();
    
    Task<int> EstimateTokenCount(string text);
    
    Task<Dictionary<string, double>> GetLanguageConfidenceScores(string text);
}