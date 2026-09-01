using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ComprovacaoFacilLattes.Infrastructure.PdfText;

/// <summary>
/// Extrai o texto completo de um PDF, preservando quebras de linha por posição —
/// o parser (<see cref="Core.Parsing.LattesPdfParser"/>) depende de a estrutura de
/// linhas do texto refletir o layout visual do PDF do Lattes, do mesmo jeito que
/// <c>PDFKit</c> fazia no app macOS original.
/// </summary>
public static class PdfTextExtractor
{
    public static string ExtractFullText(string pdfPath)
    {
        using var document = PdfDocument.Open(pdfPath);
        var sb = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            sb.Append(ContentOrderTextExtractor.GetText(page));
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
