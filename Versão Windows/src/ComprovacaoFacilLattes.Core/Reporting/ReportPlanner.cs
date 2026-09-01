using ComprovacaoFacilLattes.Core.Models;

namespace ComprovacaoFacilLattes.Core.Reporting;

/// <summary>
/// Planeja a lista de páginas ("slabs") do relatório final e a paginação do sumário —
/// a parte 100% portável de <c>PDFReportGenerator.swift</c> (o DESENHO de cada slab
/// fica na camada Infrastructure, com PdfSharp).
/// </summary>
public static class ReportPlanner
{
    private const int TocLinesFirstPage = 30; // 1ª página tem cabeçalho
    private const int TocLinesPerPage = 36;

    private sealed class TocItem
    {
        public string Title = "";
        public int Level;   // 0 = seção, 1 = item
        public int BodyIndex; // posição no corpo (antes do sumário)
        public bool IsSection;
    }

    /// <summary>Monta a lista final de slabs (sumário + corpo) já na ordem de impressão. Lista vazia = nada a gerar.</summary>
    public static List<ReportSlab> Plan(ReportConfig config, IPdfPageCounter pageCounter)
    {
        var body = new List<ReportSlab>();
        var toc = new List<TocItem>();

        // 1 — Currículo Lattes
        if (config.IncludeLattes)
        {
            var count = pageCounter.GetPageCount(config.Profile.PdfPath);
            if (count > 0)
            {
                toc.Add(new TocItem { Title = "Currículo Lattes (completo)", Level = 0, BodyIndex = body.Count, IsSection = true });
                for (var i = 0; i < count; i++)
                    body.Add(new ExternalPageSlab { SourcePdfPath = config.Profile.PdfPath, PageIndex = i });
            }
        }

        // 2 — Seções e comprovantes
        var sections = config.Profile.SortedSections.Where(s =>
            config.SelectedSectionTitles.Count == 0 || config.SelectedSectionTitles.Contains(s.Title));

        foreach (var section in sections)
        {
            var entries = FilteredEntries(section.SortedEntries, config)
                .Where(e => e.ConfirmedCertificates.Any()).ToList();
            if (entries.Count == 0) continue;

            // Página de legenda (divisória) da seção — contada, mas sem número impresso
            var sectionTitle = section.Title;
            toc.Add(new TocItem { Title = sectionTitle, Level = 0, BodyIndex = body.Count, IsSection = true });
            body.Add(new DividerSlab { Title = sectionTitle });

            foreach (var entry in entries)
            {
                var label = entry.DisplayTitle;
                config.QualisByEntry.TryGetValue(entry.Id, out var qualis);
                toc.Add(new TocItem { Title = label, Level = 1, BodyIndex = body.Count, IsSection = false });
                body.Add(new EntryHeaderSlab
                {
                    SectionTitle = entry.Section?.Title ?? "",
                    Qualis = qualis,
                    EntryDisplayTitle = entry.DisplayTitle,
                    Authors = entry.Authors,
                });

                foreach (var cert in entry.ConfirmedCertificates.Where(c => c.Exists))
                {
                    if (cert.IsPdf)
                    {
                        var certCount = pageCounter.GetPageCount(cert.FilePath);
                        for (var i = 0; i < certCount; i++)
                            body.Add(new ExternalPageSlab { SourcePdfPath = cert.FilePath, PageIndex = i });
                    }
                    else if (cert.IsImage)
                    {
                        body.Add(new ImageSlab { ImagePath = cert.FilePath });
                    }
                }
            }
        }

        if (body.Count == 0) return new List<ReportSlab>();

        // 3 — Sumário (opcional; quando presente, precisa do nº de páginas que ele
        // próprio ocupa para numerar os itens do corpo corretamente)
        var tocSlabs = config.IncludeToc ? BuildTocSlabs(toc) : new List<ReportSlab>();

        // 4 — Montagem final (a numeração em si é aplicada na renderização)
        var all = new List<ReportSlab>(tocSlabs.Count + body.Count);
        all.AddRange(tocSlabs);
        all.AddRange(body);
        return all;
    }

    // MARK: - Filtro por período

    private static IEnumerable<LattesEntry> FilteredEntries(IEnumerable<LattesEntry> entries, ReportConfig config) =>
        entries.Where(entry =>
        {
            if (config.StartYear is { } s && entry.Year > 0 && entry.Year < s) return false;
            if (config.EndYear is { } en && entry.Year > 0 && entry.Year > en) return false;
            return true;
        });

    // MARK: - Sumário

    private static int TocPageCount(int count)
    {
        if (count <= TocLinesFirstPage) return 1;
        return 1 + (int)Math.Ceiling((count - TocLinesFirstPage) / (double)TocLinesPerPage);
    }

    private static List<ReportSlab> BuildTocSlabs(List<TocItem> toc)
    {
        // Número final de cada item = páginas do sumário + posição no corpo + 1
        var tocCount = TocPageCount(toc.Count);
        var lines = toc.Select(t => new TocLine
        {
            Text = t.Title, Page = tocCount + t.BodyIndex + 1, Level = t.Level, IsSection = t.IsSection,
        }).ToList();

        // Pagina as linhas
        var chunks = new List<List<TocLine>>();
        var idx = 0;
        while (idx < lines.Count)
        {
            var cap = chunks.Count == 0 ? TocLinesFirstPage : TocLinesPerPage;
            var end = Math.Min(idx + cap, lines.Count);
            chunks.Add(lines.GetRange(idx, end - idx));
            idx = end;
        }
        if (chunks.Count == 0) chunks.Add(new List<TocLine>());

        return chunks.Select((chunk, pageIdx) => (ReportSlab)new TocPageSlab
        {
            ShowHeader = pageIdx == 0,
            Lines = chunk,
        }).ToList();
    }
}
