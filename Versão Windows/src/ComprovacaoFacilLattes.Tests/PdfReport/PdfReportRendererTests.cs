using ComprovacaoFacilLattes.Core.Models;
using ComprovacaoFacilLattes.Core.Reporting;
using ComprovacaoFacilLattes.Infrastructure.PdfReport;
using UglyToad.PdfPig;

namespace ComprovacaoFacilLattes.Tests.PdfReport;

public class PdfReportRendererTests
{
    private static string FindCalibrationPdf()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curriculo CAlibrar 2.pdf")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Curriculo CAlibrar 2.pdf");
    }

    /// <summary>
    /// Ponta a ponta com um PDF real: usa o próprio PDF de calibração como currículo
    /// Lattes E como "comprovante" de uma entrada fictícia (é um PDF válido de
    /// verdade — serve para testar a cópia/encaixe de páginas externas sem precisar
    /// de um segundo arquivo). Gera o relatório de verdade e o salva no diretório de
    /// saída dos testes para inspeção visual manual.
    /// </summary>
    [Fact]
    public void GeraUmPdfRealComSumarioLattesDivisoriaCabecalhoEComprovante()
    {
        var pdfPath = FindCalibrationPdf();

        var profile = new LattesProfile("Vítor de Teste", pdfPath, "/tmp/saves");
        var section = new LattesSection("Artigos completos publicados em periódicos", 0);
        var entry = new LattesEntry("raw", "Um Estudo de Teste", kind: "Artigo", year: 2024, authors: "TESTE, F.");
        var cert = new Certificate(pdfPath) { IsConfirmed = true };
        entry.Certificates.Add(cert);
        section.Entries.Add(entry);
        profile.Sections.Add(section);

        var config = new ReportConfig
        {
            Profile = profile,
            IncludeLattes = true,
            IncludeToc = true,
            NumberPages = true,
            QualisByEntry = new Dictionary<Guid, string> { [entry.Id] = "A1" },
        };

        var counter = new PdfPigPageCounter();
        var lattesPageCount = counter.GetPageCount(pdfPath);
        var slabs = ReportPlanner.Plan(config, counter);

        // sumário (1) + Lattes completo + divisória + cabeçalho + páginas do "comprovante" (mesmo PDF)
        var expectedCount = 1 + lattesPageCount + 1 + 1 + lattesPageCount;
        Assert.Equal(expectedCount, slabs.Count);

        var pdfBytes = PdfReportRenderer.Render(slabs, config.NumberPages);
        Assert.NotNull(pdfBytes);
        Assert.True(pdfBytes!.Length > 1000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdfBytes, 0, 4));

        var outPath = Path.Combine(AppContext.BaseDirectory, "relatorio_teste.pdf");
        File.WriteAllBytes(outPath, pdfBytes);

        using var generated = PdfDocument.Open(pdfBytes);
        Assert.Equal(expectedCount, generated.NumberOfPages);

        // A primeira página copiada do Lattes deve conter texto reconhecível do PDF original.
        var lattesPageInReport = generated.GetPage(2); // página 1 = sumário, página 2 = 1ª do Lattes
        Assert.Contains("Victor", lattesPageInReport.Text);
    }
}
