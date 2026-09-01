using ComprovacaoFacilLattes.Core.Models;
using ComprovacaoFacilLattes.Core.Qualis;
using ComprovacaoFacilLattes.Core.Reporting;
using ComprovacaoFacilLattes.Infrastructure.PdfReport;

namespace ComprovacaoFacilLattes.App.Services;

public static class ReportService
{
    public static byte[]? Generate(LattesProfile profile, ReportConfig config, QualisService qualis)
    {
        // A classificação Qualis de cada artigo é pré-calculada ANTES de gerar.
        foreach (var section in profile.Sections)
        foreach (var entry in section.Entries)
        {
            if (entry.Kind != "Artigo") continue;
            var result = qualis.Classify(entry.Venue, entry.Issn, entry.Year);
            if (result is not null) config.QualisByEntry[entry.Id] = result.Estrato;
        }

        var slabs = ReportPlanner.Plan(config, new PdfPigPageCounter());
        return PdfReportRenderer.Render(slabs, config.NumberPages);
    }
}
