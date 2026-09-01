using PDFtoImage;
using SkiaSharp;

namespace ComprovacaoFacilLattes.Infrastructure.PdfReport;

/// <summary>Rasteriza a primeira página de um PDF como PNG — usado para o preview de comprovantes na UI (não precisa de páginas seguintes; a coluna direita mostra só a primeira).</summary>
public static class PdfPagePreviewRenderer
{
    public static byte[]? RenderFirstPageAsPng(string pdfPath, int dpi = 120)
    {
        try
        {
            var bytes = File.ReadAllBytes(pdfPath);
            using var bitmap = Conversion.ToImage(bytes, 0, password: null, new RenderOptions(Dpi: dpi));
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 90);
            return png.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
