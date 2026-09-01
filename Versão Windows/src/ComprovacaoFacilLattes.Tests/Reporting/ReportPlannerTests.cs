using ComprovacaoFacilLattes.Core.Models;
using ComprovacaoFacilLattes.Core.Reporting;

namespace ComprovacaoFacilLattes.Tests.Reporting;

internal sealed class FakePageCounter : IPdfPageCounter
{
    public Dictionary<string, int> Pages { get; } = new();
    public int GetPageCount(string pdfPath) => Pages.GetValueOrDefault(pdfPath, 0);
}

/// <summary>
/// <see cref="ReportPlanner"/> só inclui certificados cujo arquivo REALMENTE existe no
/// disco (<c>Certificate.Exists</c>, igual ao <c>cert.exists</c> do app original) — os
/// testes usam arquivos vazios reais num diretório temporário em vez de caminhos
/// inventados.
/// </summary>
public class ReportPlannerTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("cfl-report-planner-tests-").FullName;

    private string TempFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    /// <summary>Perfil com 1 seção, 1 entrada com 1 certificado PDF confirmado (2 páginas) — o cenário mínimo "feliz".</summary>
    private (LattesProfile Profile, FakePageCounter Counter) MinimalProfile(int certPages = 2, int lattesPages = 3, int entryYear = 2024)
    {
        var lattesPath = TempFile("curriculo.pdf");
        var certPath = TempFile("cert.pdf");

        var profile = new LattesProfile("Fulano", lattesPath, "/tmp/saves");
        var section = new LattesSection("Artigos completos publicados em periódicos", 0);
        var entry = new LattesEntry("raw", "Título do Artigo", kind: "Artigo", year: entryYear);
        var cert = new Certificate(certPath) { IsConfirmed = true };
        entry.Certificates.Add(cert);
        section.Entries.Add(entry);
        profile.Sections.Add(section);

        var counter = new FakePageCounter();
        counter.Pages[lattesPath] = lattesPages;
        counter.Pages[certPath] = certPages;
        return (profile, counter);
    }

    [Fact]
    public void PlanoFelizProduzLattesDivisoriaCabecalhoEPaginasDoCertificadoNessaOrdem()
    {
        var (profile, counter) = MinimalProfile(certPages: 2, lattesPages: 3);
        var config = new ReportConfig { Profile = profile, IncludeToc = false };

        var slabs = ReportPlanner.Plan(config, counter);

        // Sem sumário: 3 (Lattes) + 1 (divisória) + 1 (cabeçalho) + 2 (cert) = 7
        Assert.Equal(7, slabs.Count);
        Assert.All(slabs.Take(3), s => Assert.IsType<ExternalPageSlab>(s));
        Assert.IsType<DividerSlab>(slabs[3]);
        Assert.False(slabs[3].ShowsNumber);
        Assert.IsType<EntryHeaderSlab>(slabs[4]);
        Assert.True(slabs[4].ShowsNumber);
        Assert.IsType<ExternalPageSlab>(slabs[5]);
        Assert.IsType<ExternalPageSlab>(slabs[6]);
    }

    [Fact]
    public void SumarioVemAntesDoCorpoNaListaFinal()
    {
        var (profile, counter) = MinimalProfile();
        var config = new ReportConfig { Profile = profile, IncludeToc = true };

        var slabs = ReportPlanner.Plan(config, counter);

        Assert.IsType<TocPageSlab>(slabs[0]);
        // logo depois do sumário (1 página, poucos itens) vem o corpo (Lattes primeiro)
        Assert.IsType<ExternalPageSlab>(slabs[1]);
    }

    [Fact]
    public void NumeracaoDoSumarioContaAsPropriasPaginasDoSumario()
    {
        var (profile, counter) = MinimalProfile();
        var config = new ReportConfig { Profile = profile, IncludeToc = true };

        var slabs = ReportPlanner.Plan(config, counter);
        var toc = (TocPageSlab)slabs[0];

        // 1 página de sumário (poucos itens) + corpo começa no índice 0 → primeira
        // entrada do sumário ("Currículo Lattes") deve apontar pra página 2 (1 do
        // sumário + 0 do body index + 1).
        Assert.Equal("Currículo Lattes (completo)", toc.Lines[0].Text);
        Assert.Equal(2, toc.Lines[0].Page);
    }

    [Fact]
    public void PaginaOSumarioEm30LinhasNaPrimeiraE36NasSeguintes()
    {
        var profile = new LattesProfile("Fulano", TempFile("c.pdf"), "/tmp/s");
        var section = new LattesSection("Seção Única", 0);
        var counter = new FakePageCounter();

        // 40 entradas, todas com 1 cert confirmado → 40 itens de nível 1 + 1 de seção = 41 linhas de sumário.
        for (var i = 0; i < 40; i++)
        {
            var certPath = TempFile($"cert{i}.pdf");
            var entry = new LattesEntry("raw", $"Entrada {i}", kind: "Artigo", year: 2020, order: i);
            entry.Certificates.Add(new Certificate(certPath) { IsConfirmed = true });
            section.Entries.Add(entry);
            counter.Pages[certPath] = 1;
        }
        profile.Sections.Add(section);

        var config = new ReportConfig { Profile = profile, IncludeLattes = false, IncludeToc = true };
        var slabs = ReportPlanner.Plan(config, counter);

        var tocPages = slabs.TakeWhile(s => s is TocPageSlab).Cast<TocPageSlab>().ToList();
        // 41 linhas: 30 na primeira + 11 na segunda (36 caberiam, mas só sobram 11) = 2 páginas.
        Assert.Equal(2, tocPages.Count);
        Assert.Equal(30, tocPages[0].Lines.Count);
        Assert.Equal(11, tocPages[1].Lines.Count);
        Assert.True(tocPages[0].ShowHeader);
        Assert.False(tocPages[1].ShowHeader);
    }

    [Fact]
    public void FiltraEntradasForaDoPeriodoMasMantemEntradasSemAno()
    {
        var profile = new LattesProfile("Fulano", TempFile("c.pdf"), "/tmp/s");
        var section = new LattesSection("Seção", 0);
        var counter = new FakePageCounter();

        var dentroPath = TempFile("dentro.pdf");
        var dentro = new LattesEntry("raw", "Dentro do período", kind: "Artigo", year: 2022);
        dentro.Certificates.Add(new Certificate(dentroPath) { IsConfirmed = true });

        var foraPath = TempFile("fora.pdf");
        var fora = new LattesEntry("raw", "Fora do período", kind: "Artigo", year: 2010);
        fora.Certificates.Add(new Certificate(foraPath) { IsConfirmed = true });

        var semAnoPath = TempFile("semano.pdf");
        var semAno = new LattesEntry("raw", "Sem ano", kind: "Artigo", year: 0);
        semAno.Certificates.Add(new Certificate(semAnoPath) { IsConfirmed = true });

        section.Entries.Add(dentro);
        section.Entries.Add(fora);
        section.Entries.Add(semAno);
        profile.Sections.Add(section);

        counter.Pages[dentroPath] = 1;
        counter.Pages[foraPath] = 1;
        counter.Pages[semAnoPath] = 1;

        var config = new ReportConfig
        {
            Profile = profile, IncludeLattes = false, IncludeToc = false,
            StartYear = 2020, EndYear = 2025,
        };
        var slabs = ReportPlanner.Plan(config, counter);

        var headers = slabs.OfType<EntryHeaderSlab>().Select(h => h.EntryDisplayTitle).ToList();
        Assert.Contains(headers, t => t.Contains("Dentro do período"));
        Assert.Contains(headers, t => t.Contains("Sem ano"));
        Assert.DoesNotContain(headers, t => t.Contains("Fora do período"));
    }

    [Fact]
    public void EntradaSemCertificadoConfirmadoNaoGeraPaginas()
    {
        var profile = new LattesProfile("Fulano", TempFile("c.pdf"), "/tmp/s");
        var section = new LattesSection("Seção", 0);
        var entry = new LattesEntry("raw", "Sem confirmação", kind: "Artigo", year: 2024);
        entry.Certificates.Add(new Certificate(TempFile("naoconfirmado.pdf")) { IsConfirmed = false });
        section.Entries.Add(entry);
        profile.Sections.Add(section);

        var config = new ReportConfig { Profile = profile, IncludeLattes = false, IncludeToc = false };
        var slabs = ReportPlanner.Plan(config, new FakePageCounter());

        Assert.Empty(slabs);
    }

    [Fact]
    public void CertificadoConfirmadoCujoArquivoNaoExisteMaisNaoGeraPaginasDeCertificadoMasAEntradaAindaAparece()
    {
        // Igual ao app original: o filtro de "tem certificado confirmado" olha só o
        // registro (não o disco) — a entrada e seu cabeçalho aparecem, só as páginas
        // do PRÓPRIO certificado (uma por uma) são puladas se o arquivo sumiu.
        var profile = new LattesProfile("Fulano", TempFile("c.pdf"), "/tmp/s");
        var section = new LattesSection("Seção", 0);
        var entry = new LattesEntry("raw", "Arquivo apagado", kind: "Artigo", year: 2024);
        // Caminho válido só de sintaxe — nunca criado no disco.
        entry.Certificates.Add(new Certificate(Path.Combine(_tempDir, "fantasma.pdf")) { IsConfirmed = true });
        section.Entries.Add(entry);
        profile.Sections.Add(section);

        var config = new ReportConfig { Profile = profile, IncludeLattes = false, IncludeToc = false };
        var slabs = ReportPlanner.Plan(config, new FakePageCounter());

        Assert.Equal(2, slabs.Count); // divisória da seção + cabeçalho da entrada
        Assert.IsType<DividerSlab>(slabs[0]);
        Assert.IsType<EntryHeaderSlab>(slabs[1]);
    }

    [Fact]
    public void FiltraPorSecoesSelecionadas()
    {
        var profile = new LattesProfile("Fulano", TempFile("c.pdf"), "/tmp/s");
        var counter = new FakePageCounter();

        var secA = new LattesSection("Seção A", 0);
        var aPath = TempFile("a.pdf");
        var entryA = new LattesEntry("raw", "Item A", kind: "Artigo", year: 2024);
        entryA.Certificates.Add(new Certificate(aPath) { IsConfirmed = true });
        secA.Entries.Add(entryA);

        var secB = new LattesSection("Seção B", 1);
        var bPath = TempFile("b.pdf");
        var entryB = new LattesEntry("raw", "Item B", kind: "Artigo", year: 2024);
        entryB.Certificates.Add(new Certificate(bPath) { IsConfirmed = true });
        secB.Entries.Add(entryB);

        profile.Sections.Add(secA);
        profile.Sections.Add(secB);

        counter.Pages[aPath] = 1;
        counter.Pages[bPath] = 1;

        var config = new ReportConfig
        {
            Profile = profile, IncludeLattes = false, IncludeToc = false,
            SelectedSectionTitles = new HashSet<string> { "Seção A" },
        };
        var slabs = ReportPlanner.Plan(config, counter);

        var dividers = slabs.OfType<DividerSlab>().Select(d => d.Title).ToList();
        Assert.Equal(new[] { "Seção A" }, dividers);
    }

    [Fact]
    public void SemNadaParaIncluirRetornaListaVazia()
    {
        var profile = new LattesProfile("Fulano", TempFile("c.pdf"), "/tmp/s");
        var config = new ReportConfig { Profile = profile, IncludeLattes = false, IncludeToc = true };

        var slabs = ReportPlanner.Plan(config, new FakePageCounter());

        Assert.Empty(slabs);
    }
}
