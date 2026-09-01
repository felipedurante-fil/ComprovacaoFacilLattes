using ComprovacaoFacilLattes.Infrastructure.PdfText;
using SkiaSharp;

namespace ComprovacaoFacilLattes.Tests.PdfText;

public class CertificateTextExtractorTests
{
    /// <summary>Gera uma imagem sintética com texto desenhado e confirma que o Tesseract (OCR real, não simulado) consegue lê-lo de volta — valida a integração ponta-a-ponta com os dados de idioma baixados em TessData.</summary>
    [Fact]
    public void OcrImageBytesLeTextoDeUmaImagemSintetica()
    {
        using var bitmap = new SKBitmap(900, 200);
        using (var canvas = new SKCanvas(bitmap))
        using (var font = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright), 48))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawText("CERTIFICADO DE PARTICIPACAO", 20, 100, SKTextAlign.Left, font, paint);
        }
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);

        var text = CertificateTextExtractor.OcrImageBytes(png.ToArray());

        Assert.Contains("CERTIFICADO", text.ToUpperInvariant());
    }

    [Fact]
    public void ExtractTextFromPdfUsaACamadaDeTextoNativoQuandoDisponivel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curriculo CAlibrar 2.pdf")))
            dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "Curriculo CAlibrar 2.pdf");

        var extraction = CertificateTextExtractor.ExtractTextFromPdf(path);

        Assert.False(extraction.UsedOcr);
        Assert.Contains("Victor", extraction.Text);
    }
}
