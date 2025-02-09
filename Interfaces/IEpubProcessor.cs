namespace genslation.Interfaces;

using genslation.Models;

public interface IEpubProcessor
{
    Task<EpubDocument> LoadAsync(string filePath);
    
    Task<bool> ValidateStructureAsync(EpubDocument document);
    
    Task<List<EpubChapter>> ExtractChaptersAsync(EpubDocument document);
    
    Task<bool> SaveTranslatedEpubAsync(
        EpubDocument originalDocument,
        string outputPath,
        TranslationOptions options);
        
    Task<Dictionary<string, string>> ExtractMetadataAsync(string filePath);
    
    Task<bool> ValidateOutputAsync(string filePath);
    
    Task<byte[]> GeneratePreviewAsync(EpubDocument document, int chapterIndex = 0);
}