using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using genslation.Interfaces;
using genslation.Models;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf;
using iText.Kernel.Font;
using Microsoft.Extensions.Logging;

namespace genslation.Services
{
    public class PdfProcessor
    {
        private readonly ILogger<PdfProcessor> _logger;
        private readonly ITranslationProvider _translationProvider;

        public PdfProcessor(ILogger<PdfProcessor> logger, ITranslationProvider translationProvider)
        {
            _logger = logger;
            _translationProvider = translationProvider;
        }

        public async Task<PdfDocumentModel> LoadAsync(string filePath)
        {
            try
            {
                var document = new PdfDocumentModel
                {
                    FilePath = filePath,
                    Pages = new List<PdfPageModel>()
                };

                using var pdfReader = new PdfReader(filePath);
                using var pdfDocument = new PdfDocument(pdfReader);

                for (int i = 1; i <= pdfDocument.GetNumberOfPages(); i++)
                {
                    var page = pdfDocument.GetPage(i);
                    var text = PdfTextExtractor.GetTextFromPage(page);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        _logger.LogWarning("Page {PageNumber} contains no extractable text.", i);
                        text = "[No extractable text]";
                    }
                    document.Pages.Add(new PdfPageModel
                    {
                        PageNumber = i,
                        OriginalText = text
                    });
                }

                return document;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load PDF file: {FilePath}", filePath);
                throw;
            }
        }

        public async Task<PdfDocumentModel> TranslateDocumentAsync(PdfDocumentModel document, string targetLanguage, TranslationOptions options)
        {
            foreach (var page in document.Pages)
            {
                var translationResult = await _translationProvider.TranslateAsync(
                    page.OriginalText,
                    document.Language,
                    targetLanguage,
                    options);

                page.TranslatedText = translationResult.TranslatedContent;
            }

            return document;
        }

        public async Task SaveTranslatedPdfAsync(PdfDocumentModel document, string outputPath)
        {
            try
            {
                using var pdfWriter = new PdfWriter(outputPath);
                using var pdfDocument = new PdfDocument(pdfWriter);

                foreach (var page in document.Pages)
                {
                    var pdfPage = pdfDocument.AddNewPage();
                    var canvas = new iText.Kernel.Pdf.Canvas.PdfCanvas(pdfPage);
                    canvas.BeginText();
                    var font = iText.Kernel.Font.PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);
                    canvas.SetFontAndSize(font, 12);
                    canvas.MoveText(36, pdfPage.GetPageSize().GetHeight() - 36);

                    var translatedText = page.TranslatedText ?? page.OriginalText;
                    canvas.ShowText(translatedText);
                    canvas.EndText();
                }

                _logger.LogInformation("Successfully saved translated PDF: {OutputPath}", outputPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save translated PDF: {OutputPath}", outputPath);
                throw;
            }
        }
    }

    public class PdfDocumentModel : IDocumentModel
    {
        public string FilePath { get; set; }
        public string Language { get; set; }
        public List<PdfPageModel> Pages { get; set; }
        public TranslationMetrics? TranslationMetrics { get; set; }
    }

    public class PdfPageModel
    {
        public int PageNumber { get; set; }
        public string OriginalText { get; set; }
        public string TranslatedText { get; set; }
    }
}