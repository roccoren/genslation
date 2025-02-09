namespace genslation.Interfaces;

using genslation.Models;

public interface ITranslationMemoryService
{
    Task<TranslationMemoryEntry?> FindMatchAsync(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        double minimumSimilarity = 0.9);
        
    Task StoreTranslationAsync(TranslationMemoryEntry entry);
    
    Task<List<TranslationMemoryEntry>> FindSimilarEntriesAsync(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        int maxResults = 5);
        
    Task<bool> UpdateEntryAsync(TranslationMemoryEntry entry);
    
    Task<Dictionary<string, int>> GetStatisticsAsync();
    
    Task ImportMemoryAsync(string filePath);
    
    Task ExportMemoryAsync(string filePath);
    
    Task<bool> OptimizeStorageAsync();

    Task<double> CalculateSimilarityAsync(string text1, string text2);

    Task<bool> DeleteEntryAsync(string sourceText, string sourceLanguage, string targetLanguage);

    Task<List<string>> GetSupportedLanguagesAsync();

    Task ClearMemoryAsync(string? sourceLanguage = null, string? targetLanguage = null);
}