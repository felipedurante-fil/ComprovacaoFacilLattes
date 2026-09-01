using ComprovacaoFacilLattes.Core.Reporting;
using UglyToad.PdfPig;

namespace ComprovacaoFacilLattes.Infrastructure.PdfReport;

/// <summary>Implementação de <see cref="IPdfPageCounter"/> via PdfPig.</summary>
public sealed class PdfPigPageCounter : IPdfPageCounter
{
    public int GetPageCount(string pdfPath)
    {
        try
        {
            using var document = PdfDocument.Open(pdfPath);
            return document.NumberOfPages;
        }
        catch
        {
            return 0;
        }
    }
}
