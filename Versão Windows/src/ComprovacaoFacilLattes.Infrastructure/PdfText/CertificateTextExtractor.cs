using System.Text;
using PDFtoImage;
using SkiaSharp;
using Tesseract;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ComprovacaoFacilLattes.Infrastructure.PdfText;

public readonly record struct Extraction(string Text, bool UsedOcr);

/// <summary>
/// Extração de texto de comprovantes (PDF nativo com fallback para OCR, ou OCR direto
/// para imagens) — equivalente a <c>CertificateIndexer.extractText/extractTextFromPDF/
/// ocrImage/renderPageToCGImage</c> do app macOS (que usava PDFKit+Vision, Apple-only),
/// usando PdfPig (texto nativo), PDFtoImage/PDFium (rasterização) e Tesseract (OCR) —
/// todos cross-platform.
///
/// Limitação conhecida: imagens HEIC não são decodificadas pelo Leptonica/Tesseract
/// (formato não suportado nativamente fora do ecossistema Apple) — ficam sem OCR.
/// </summary>
public static class CertificateTextExtractor
{
    private static readonly HashSet<string> ImageExtensions = new()
    {
        "jpg", "jpeg", "png", "tiff", "tif", "heic",
    };

    /// <summary>Pasta com os arquivos <c>*.traineddata</c> (por/eng). Padrão: subpasta "TessData" ao lado do executável.</summary>
    public static string TessDataPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "TessData");

    public static Extraction ExtractText(string filePath)
    {
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (ext == "pdf") return ExtractTextFromPdf(filePath);
        if (ImageExtensions.Contains(ext))
        {
            try
            {
                return new Extraction(OcrImageBytes(File.ReadAllBytes(filePath)), true);
            }
            catch
            {
                return new Extraction("", true);
            }
        }
        return new Extraction("", false);
    }

    public static Extraction ExtractTextFromPdf(string pdfPath)
    {
        string nativeText;
        int pageCount;
        try
        {
            using var document = PdfDocument.Open(pdfPath);
            pageCount = document.NumberOfPages;
            var sb = new StringBuilder();
            var pagesToRead = Math.Min(pageCount, 5);
            for (var i = 1; i <= pagesToRead; i++)
            {
                sb.Append(ContentOrderTextExtractor.GetText(document.GetPage(i)));
                sb.Append('\n');
            }
            nativeText = sb.ToString();
        }
        catch
        {
            return new Extraction("", false);
        }

        // 1 — a camada de texto (PDF "nativo") já basta.
        if (nativeText.Trim().Length >= 20) return new Extraction(nativeText, false);

        // 2 — PDF digitalizado (sem texto): renderiza páginas e aplica OCR.
        var ocrSb = new StringBuilder();
        try
        {
            var bytes = File.ReadAllBytes(pdfPath);
            var pagesToOcr = Math.Min(pageCount, 4);
            var options = new RenderOptions(Dpi: 300);
            for (var i = 0; i < pagesToOcr; i++)
            {
                using var bitmap = Conversion.ToImage(bytes, i, password: null, options);
                using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                ocrSb.Append(OcrImageBytes(png.ToArray()));
                ocrSb.Append('\n');
            }
        }
        catch
        {
            // páginas que falharem na rasterização/OCR são ignoradas — texto parcial é aceitável
        }
        return new Extraction(ocrSb.ToString(), true);
    }

    /// <summary>OCR síncrono via Tesseract (bloqueia até concluir — chamar fora da thread de UI).</summary>
    public static string OcrImageBytes(byte[] imageBytes)
    {
        try
        {
            using var engine = new TesseractEngine(TessDataPath, "por+eng", EngineMode.Default);
            using var pix = Pix.LoadFromMemory(imageBytes);
            using var page = engine.Process(pix);
            return page.GetText().Trim();
        }
        catch
        {
            return "";
        }
    }
}
