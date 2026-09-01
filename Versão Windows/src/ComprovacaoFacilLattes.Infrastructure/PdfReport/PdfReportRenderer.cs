using ComprovacaoFacilLattes.Core.Reporting;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace ComprovacaoFacilLattes.Infrastructure.PdfReport;

/// <summary>
/// Desenha a lista de <see cref="ReportSlab"/> planejada por <c>ReportPlanner</c> (Core)
/// num PDF A4 retrato — equivalente ao desenho de <c>PDFReportGenerator.swift</c>
/// (CoreGraphics/CoreText), usando PdfSharp/XGraphics.
///
/// Nota de coordenadas: o CoreGraphics do app original usa origem no canto INFERIOR
/// esquerdo (Y para cima); o XGraphics do PdfSharp usa origem no canto SUPERIOR
/// esquerdo (Y para baixo, como a maioria das APIs .NET/GDI). Os layouts abaixo foram
/// recalculados para o sistema Y-para-baixo — reproduzem a mesma faixa visual de cada
/// elemento, não uma tradução literal pixel-a-pixel das fórmulas originais.
/// </summary>
public static class PdfReportRenderer
{
    private const double PageWidth = 595;  // A4 retrato, pt
    private const double PageHeight = 842;

    private static readonly XColor Accent = XColor.FromArgb(64, 128, 230);
    private static readonly XColor PaleBg = XColor.FromArgb(245, 247, 255);

    /// <summary>Gera o PDF final; <c>null</c> se não houver nada para desenhar.</summary>
    public static byte[]? Render(List<ReportSlab> slabs, bool numberPages)
    {
        if (slabs.Count == 0) return null;

        EmbeddedFontResolver.EnsureRegistered();

        using var document = new PdfDocument();

        for (var i = 0; i < slabs.Count; i++)
        {
            var slab = slabs[i];
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(PageWidth);
            page.Height = XUnit.FromPoint(PageHeight);

            using (var gfx = XGraphics.FromPdfPage(page))
            {
                switch (slab)
                {
                    case ExternalPageSlab ext: DrawFittedExternalPage(gfx, ext); break;
                    case ImageSlab img: DrawCertificateImage(gfx, img); break;
                    case DividerSlab div: DrawDivider(gfx, div.Title); break;
                    case EntryHeaderSlab hdr: DrawEntryHeader(gfx, hdr); break;
                    case TocPageSlab toc: DrawTocPage(gfx, toc); break;
                }

                // O número só é impresso quando a opção está ligada E a página permite
                // (divisórias de seção nunca mostram número, mesmo com a opção ligada).
                if (numberPages && slab.ShowsNumber) DrawPageNumber(gfx, i + 1);
            }
        }

        using var ms = new MemoryStream();
        document.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    // MARK: - Páginas externas (Lattes completo / comprovantes)

    /// <summary>Encaixa uma página de PDF externo em A4 retrato, preservando proporção.</summary>
    private static void DrawFittedExternalPage(XGraphics gfx, ExternalPageSlab slab)
    {
        try
        {
            var form = XPdfForm.FromFile(slab.SourcePdfPath);
            form.PageIndex = slab.PageIndex;
            var vw = form.PointWidth;
            var vh = form.PointHeight;
            if (vw <= 0 || vh <= 0) return;

            var scale = Math.Min(PageWidth / vw, PageHeight / vh);
            var dw = vw * scale;
            var dh = vh * scale;
            gfx.DrawImage(form, new XRect((PageWidth - dw) / 2, (PageHeight - dh) / 2, dw, dh));
        }
        catch
        {
            // PDF corrompido/inacessível no meio do processo — página fica em branco
            // em vez de interromper a geração do relatório inteiro.
        }
    }

    private static void DrawCertificateImage(XGraphics gfx, ImageSlab slab)
    {
        try
        {
            using var image = XImage.FromFile(slab.ImagePath);
            var iw = image.PointWidth;
            var ih = image.PointHeight;
            if (iw <= 0 || ih <= 0) return;

            var scale = Math.Min((PageWidth - 40) / iw, (PageHeight - 60) / ih);
            var dw = iw * scale;
            var dh = ih * scale;
            gfx.DrawImage(image, (PageWidth - dw) / 2, (PageHeight - dh) / 2, dw, dh);
        }
        catch
        {
            // imagem inválida/formato não suportado (ex.: HEIC) — página fica em branco.
        }
    }

    // MARK: - Páginas desenhadas

    private static void DrawDivider(XGraphics gfx, string title)
    {
        // Fundo branco (economia de tinta na impressão)
        gfx.DrawRectangle(XBrushes.White, new XRect(0, 0, PageWidth, PageHeight));

        var midTop = PageHeight / 2; // linha de referência vertical, em coordenadas Y-para-baixo

        var labelFont = new XFont("Arial", 13, XFontStyleEx.Bold);
        DrawText(gfx, "SEÇÃO", labelFont, new XSolidBrush(Accent),
            new XRect(40, midTop - 86, PageWidth - 80, 22), XStringAlignment.Center);

        gfx.DrawRectangle(new XSolidBrush(Accent), new XRect(90, midTop - 58.5, PageWidth - 180, 2.5));

        var titleFont = new XFont("Arial", 26, XFontStyleEx.Bold);
        DrawText(gfx, title, titleFont, XBrushes.Black,
            new XRect(50, midTop - 50, PageWidth - 100, 120), XStringAlignment.Center);
    }

    private static void DrawEntryHeader(XGraphics gfx, EntryHeaderSlab entry)
    {
        gfx.DrawRectangle(new XSolidBrush(PaleBg), new XRect(0, 0, PageWidth, PageHeight));
        gfx.DrawRectangle(new XSolidBrush(Accent), new XRect(0, 0, PageWidth, 6));

        var header = entry.SectionTitle;
        if (!string.IsNullOrEmpty(entry.Qualis)) header += $"   •   Qualis {entry.Qualis}";
        DrawText(gfx, header, new XFont("Arial", 13), XBrushes.Gray,
            new XRect(60, 60, PageWidth - 120, 20), XStringAlignment.Near);

        DrawText(gfx, entry.EntryDisplayTitle, new XFont("Arial", 18, XFontStyleEx.Bold), XBrushes.Black,
            new XRect(60, 90, PageWidth - 120, 140), XStringAlignment.Near);

        if (!string.IsNullOrEmpty(entry.Authors))
        {
            DrawText(gfx, entry.Authors, new XFont("Arial", 12), XBrushes.DarkGray,
                new XRect(60, 250, PageWidth - 120, 70), XStringAlignment.Near);
        }
    }

    // MARK: - Sumário

    private const double TocLineHeight = 19;

    private static void DrawTocPage(XGraphics gfx, TocPageSlab tocPage)
    {
        gfx.DrawRectangle(XBrushes.White, new XRect(0, 0, PageWidth, PageHeight));

        var y = 56.0;
        if (tocPage.ShowHeader)
        {
            DrawText(gfx, "Sumário", new XFont("Arial", 24, XFontStyleEx.Bold), XBrushes.Black,
                new XRect(50, y, PageWidth - 100, 30), XStringAlignment.Near);
            y += 56;
        }

        foreach (var line in tocPage.Lines)
        {
            var indent = line.Level == 0 ? 50 : 78;
            var font = line.IsSection
                ? new XFont("Arial", 12.5, XFontStyleEx.Bold)
                : new XFont("Arial", 11);
            var brush = line.IsSection ? XBrushes.Black : XBrushes.DarkGray;

            DrawText(gfx, line.Text, font, brush,
                new XRect(indent, y, PageWidth - indent - 80, TocLineHeight), XStringAlignment.Near, truncate: true);
            DrawText(gfx, line.Page.ToString(), font, XBrushes.Black,
                new XRect(PageWidth - 74, y, 56, TocLineHeight), XStringAlignment.Far);

            y += TocLineHeight;
        }
    }

    // MARK: - Numeração

    /// <summary>Número de página: caixa preta no canto superior direito, número branco (garante legibilidade em qualquer fundo).</summary>
    private static void DrawPageNumber(XGraphics gfx, int number)
    {
        var label = number.ToString();
        const double boxH = 22;
        var boxW = Math.Max(30, label.Length * 9 + 16);
        const double margin = 16;
        var rect = new XRect(PageWidth - boxW - margin, margin, boxW, boxH);

        gfx.DrawRoundedRectangle(XPens.White, new XSolidBrush(XColors.Black), rect, new XSize(4, 4));
        DrawText(gfx, label, new XFont("Arial", 13, XFontStyleEx.Bold), XBrushes.White,
            new XRect(rect.X, rect.Y + 3, rect.Width, rect.Height - 6), XStringAlignment.Center);
    }

    // MARK: - Texto

    private static void DrawText(XGraphics gfx, string text, XFont font, XBrush brush, XRect rect,
        XStringAlignment alignment, bool truncate = false)
    {
        var format = new XStringFormat { Alignment = alignment, LineAlignment = XLineAlignment.Near };
        // PdfSharp não tem truncamento nativo por "…" no DrawString(rect) — para os
        // casos que pedem truncamento (linhas do sumário), corta o texto manualmente
        // por medida em vez de deixá-lo vazar/quebrar linha de forma imprevisível.
        var toDraw = truncate ? TruncateToFit(gfx, text, font, rect.Width) : text;
        gfx.DrawString(toDraw, font, brush, rect, format);
    }

    private static string TruncateToFit(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        if (gfx.MeasureString(text, font).Width <= maxWidth) return text;
        const string ellipsis = "…";
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            var candidate = text[..mid] + ellipsis;
            if (gfx.MeasureString(candidate, font).Width <= maxWidth) lo = mid; else hi = mid - 1;
        }
        return lo == 0 ? ellipsis : text[..lo] + ellipsis;
    }
}
