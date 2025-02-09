namespace genslation.Services;

using System.Collections.Concurrent;
using System.Text.Json;
using genslation.Interfaces;
using genslation.Models;
using Microsoft.Extensions.Logging;
using F23.StringSimilarity;

public class TranslationMemoryService : ITranslationMemoryService
{
    private readonly ILogger<TranslationMemoryService> _logger;
    private readonly string _storageDirectory;
    private readonly ConcurrentDictionary<string, TranslationMemoryEntry> _memoryCache;
    private readonly NormalizedLevenshtein _similarityAlgorithm;
    private readonly SemaphoreSlim _semaphore;

    public TranslationMemoryService(
        ILogger<TranslationMemoryService> logger,
        string storageDirectory = "translation_memory")
    {
        _logger = logger;
        _storageDirectory = Path.Combine(
            Path.GetDirectoryName(typeof(TranslationMemoryService).Assembly.Location) ?? ".",
            storageDirectory);
        _memoryCache = new ConcurrentDictionary<string, TranslationMemoryEntry>();
        _similarityAlgorithm = new NormalizedLevenshtein();
        _semaphore = new SemaphoreSlim(1, 1);

        Directory.CreateDirectory(_storageDirectory);
        LoadMemoryFromDisk().Wait();
    }

    public async Task<TranslationMemoryEntry?> FindMatchAsync(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        double minimumSimilarity = 0.9)
    {
        var key = GetMemoryKey(sourceText, sourceLanguage, targetLanguage);
        if (_memoryCache.TryGetValue(key, out var exactMatch))
        {
            await UpdateLastUsedAsync(exactMatch);
            return exactMatch;
        }

        var similarEntries = await FindSimilarEntriesAsync(sourceText, sourceLanguage, targetLanguage);
        return similarEntries.FirstOrDefault();
    }

    public async Task StoreTranslationAsync(TranslationMemoryEntry entry)
    {
        var key = GetMemoryKey(entry.SourceText, entry.SourceLanguage, entry.TargetLanguage);
        _memoryCache[key] = entry;
        await SaveMemoryToDiskAsync();
    }

    public async Task<List<TranslationMemoryEntry>> FindSimilarEntriesAsync(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        int maxResults = 5)
    {
        var entries = _memoryCache.Values
            .Where(e => e.SourceLanguage == sourceLanguage && 
                       e.TargetLanguage == targetLanguage)
            .Select(e => new
            {
                Entry = e,
                Similarity = _similarityAlgorithm.Similarity(sourceText, e.SourceText)
            })
            .Where(x => x.Similarity >= 0.8)
            .OrderByDescending(x => x.Similarity)
            .Take(maxResults)
            .Select(x => x.Entry)
            .ToList();

        foreach (var entry in entries)
        {
            await UpdateLastUsedAsync(entry);
        }

        return entries;
    }

    public async Task<bool> UpdateEntryAsync(TranslationMemoryEntry entry)
    {
        var key = GetMemoryKey(entry.SourceText, entry.SourceLanguage, entry.TargetLanguage);
        if (_memoryCache.TryGetValue(key, out var existingEntry))
        {
            _memoryCache[key] = entry;
            await SaveMemoryToDiskAsync();
            return true;
        }
        return false;
    }

    public async Task<Dictionary<string, int>> GetStatisticsAsync()
    {
        return new Dictionary<string, int>
        {
            { "TotalEntries", _memoryCache.Count },
            { "LanguagePairs", GetLanguagePairs().Count }
        };
    }

    public async Task ImportMemoryAsync(string filePath)
    {
        await _semaphore.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var entries = JsonSerializer.Deserialize<List<TranslationMemoryEntry>>(json);
            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    var key = GetMemoryKey(entry.SourceText, entry.SourceLanguage, entry.TargetLanguage);
                    _memoryCache[key] = entry;
                }
            }
            await SaveMemoryToDiskAsync();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task ExportMemoryAsync(string filePath)
    {
        await _semaphore.WaitAsync();
        try
        {
            var entries = _memoryCache.Values.ToList();
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(filePath, json);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool> OptimizeStorageAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            // Remove entries that haven't been used in 90 days
            var cutoffDate = DateTime.UtcNow.AddDays(-90);
            var keysToRemove = _memoryCache.Where(kvp => kvp.Value.LastUsed < cutoffDate)
                                         .Select(kvp => kvp.Key)
                                         .ToList();

            foreach (var key in keysToRemove)
            {
                _memoryCache.TryRemove(key, out _);
            }

            await SaveMemoryToDiskAsync();
            return true;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<double> CalculateSimilarityAsync(string text1, string text2)
    {
        return _similarityAlgorithm.Similarity(text1, text2);
    }

    public async Task<bool> DeleteEntryAsync(string sourceText, string sourceLanguage, string targetLanguage)
    {
        var key = GetMemoryKey(sourceText, sourceLanguage, targetLanguage);
        var result = _memoryCache.TryRemove(key, out _);
        if (result)
        {
            await SaveMemoryToDiskAsync();
        }
        return result;
    }

    public async Task<List<string>> GetSupportedLanguagesAsync()
    {
        return GetLanguagePairs().SelectMany(pair => new[] { pair.sourceLanguage, pair.targetLanguage })
                                .Distinct()
                                .ToList();
    }

    public async Task ClearMemoryAsync(string? sourceLanguage = null, string? targetLanguage = null)
    {
        await _semaphore.WaitAsync();
        try
        {
            if (sourceLanguage == null && targetLanguage == null)
            {
                _memoryCache.Clear();
            }
            else
            {
                var keysToRemove = _memoryCache.Where(kvp =>
                    (sourceLanguage == null || kvp.Value.SourceLanguage == sourceLanguage) &&
                    (targetLanguage == null || kvp.Value.TargetLanguage == targetLanguage))
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _memoryCache.TryRemove(key, out _);
                }
            }

            await SaveMemoryToDiskAsync();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private string GetMemoryKey(string sourceText, string sourceLanguage, string targetLanguage)
    {
        return $"{sourceLanguage}:{targetLanguage}:{sourceText.GetHashCode()}";
    }

    private List<(string sourceLanguage, string targetLanguage)> GetLanguagePairs()
    {
        return _memoryCache.Values
            .Select(e => (e.SourceLanguage, e.TargetLanguage))
            .Distinct()
            .ToList();
    }

    private async Task UpdateLastUsedAsync(TranslationMemoryEntry entry)
    {
        entry.LastUsed = DateTime.UtcNow;
        entry.UseCount++;
        await SaveMemoryToDiskAsync();
    }

    private async Task LoadMemoryFromDisk()
    {
        var memoryFile = Path.Combine(_storageDirectory, "translation_memory.json");
        if (File.Exists(memoryFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(memoryFile);
                var entries = JsonSerializer.Deserialize<List<TranslationMemoryEntry>>(json);
                if (entries != null)
                {
                    foreach (var entry in entries)
                    {
                        var key = GetMemoryKey(entry.SourceText, entry.SourceLanguage, entry.TargetLanguage);
                        _memoryCache[key] = entry;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load translation memory from disk");
            }
        }
    }

    private async Task SaveMemoryToDiskAsync()
    {
        var memoryFile = Path.Combine(_storageDirectory, "translation_memory.json");
        try
        {
            var entries = _memoryCache.Values.ToList();
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(memoryFile, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save translation memory to disk");
        }
    }
}