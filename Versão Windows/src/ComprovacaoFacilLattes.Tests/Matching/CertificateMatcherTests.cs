using ComprovacaoFacilLattes.Core.Matching;

namespace ComprovacaoFacilLattes.Tests.Matching;

/// <summary>Casos de scoring descritos no §5 do WINDOWS_PORT_LOGIC.md.</summary>
public class CertificateMatcherTests
{
    private static CertificateMatcher.EntryFields Entry(
        string title = "", string kind = "", string venue = "", string portaria = "",
        string edital = "", string issn = "", string doi = "", int year = 0, int endYear = 0,
        string hashKey = "hk") => new()
    {
        Title = title, Kind = kind, Venue = venue, Portaria = portaria,
        Edital = edital, Issn = issn, Doi = doi, Year = year, EndYear = endYear, HashKey = hashKey,
    };

    private static readonly Dictionary<string, double> NoIdf = new();

    [Fact]
    public void PortariaComNumeroEAnoIguaisPontua099()
    {
        var entries = new List<CertificateMatcher.EntryFields>
        {
            Entry(kind: "Vínculo institucional", portaria: "2891/2022"),
        };
        var ranked = CertificateMatcher.RankedMatches(
            "PORTARIA Nº 2891, DE 06 DE OUTUBRO DE 2022", "cert", new HashSet<int> { 2022 },
            entries, new HashSet<string>(), NoIdf, new HashSet<string>());

        Assert.Single(ranked);
        Assert.Equal(0.99, ranked[0].Score, precision: 2);
    }

    [Fact]
    public void PortariaComMesmoNumeroMasAnosDiferentesNaoPontua()
    {
        var entries = new List<CertificateMatcher.EntryFields>
        {
            Entry(kind: "Vínculo institucional", portaria: "2891/2019"),
        };
        var ranked = CertificateMatcher.RankedMatches(
            "PORTARIA Nº 2891, DE 06 DE OUTUBRO DE 2022", "cert", new HashSet<int> { 2022 },
            entries, new HashSet<string>(), NoIdf, new HashSet<string>());

        Assert.Empty(ranked);
    }

    [Fact]
    public void EditalComNumeroEAnoIguaisPontua099()
    {
        var entries = new List<CertificateMatcher.EntryFields> { Entry(edital: "41/2024") };
        var ranked = CertificateMatcher.RankedMatches(
            "Edital nº 41/2024-PROGRAD", "cert", new HashSet<int>(),
            entries, new HashSet<string>(), NoIdf, new HashSet<string>());

        Assert.Single(ranked);
        Assert.Equal(0.99, ranked[0].Score, precision: 2);
    }

    [Fact]
    public void DoiIgualPontua100()
    {
        var entries = new List<CertificateMatcher.EntryFields> { Entry(doi: "10.1234/abc") };
        var ranked = CertificateMatcher.RankedMatches(
            "disponível em 10.1234/abc", "cert", new HashSet<int>(),
            entries, new HashSet<string>(), NoIdf, new HashSet<string>());

        Assert.Single(ranked);
        Assert.Equal(1.0, ranked[0].Score, precision: 2);
    }

    [Fact]
    public void DocumentoSemIdentificadorDePublicacaoNuncaVinculaArtigoOuLivro()
    {
        var entries = new List<CertificateMatcher.EntryFields>
        {
            Entry(title: "Um Estudo Sobre Filosofia Contemporânea", kind: "Artigo"),
        };
        // Texto bate MUITO bem com o título, mas não tem ISSN/ISBN/DOI — o gate deve bloquear.
        var ranked = CertificateMatcher.RankedMatches(
            "Certificamos que o artigo Um Estudo Sobre Filosofia Contemporânea foi publicado",
            "cert", new HashSet<int>(), entries, new HashSet<string>(), NoIdf, new HashSet<string>());

        Assert.Empty(ranked);
    }

    [Fact]
    public void DocumentoDePortariaNuncaESugeridoParaOrientacaoOuFormacao()
    {
        var entries = new List<CertificateMatcher.EntryFields>
        {
            Entry(title: "Orientação de Fulano de Tal", kind: "Orientação"),
        };
        var ranked = CertificateMatcher.RankedMatches(
            "PORTARIA Nº 100, DE 2020 designa Fulano de Tal orientador", "cert",
            new HashSet<int>(), entries, new HashSet<string>(), NoIdf, new HashSet<string>());

        Assert.Empty(ranked);
    }

    [Fact]
    public void VinculoJaRejeitadoPeloUsuarioNuncaReaparece()
    {
        var entries = new List<CertificateMatcher.EntryFields>
        {
            Entry(title: "Congresso Brasileiro de Matemática Aplicada", kind: "Evento", hashKey: "2024_congresso"),
        };
        var rejected = new HashSet<string> { "certificado||2024_congresso" };
        var ranked = CertificateMatcher.RankedMatches(
            "Certificado de participação no Congresso Brasileiro de Matemática Aplicada",
            "certificado", new HashSet<int>(), entries, new HashSet<string>(), NoIdf, rejected);

        Assert.Empty(ranked);
    }

    [Fact]
    public void AnoDoCertificadoDentroDoPeriodoDeUmVinculoAbertoReforcaOScore()
    {
        var entries = new List<CertificateMatcher.EntryFields>
        {
            Entry(title: "Professor Assistente na Universidade Federal do Acre — Vínculo institucional",
                kind: "Vínculo institucional", venue: "Universidade Federal do Acre",
                year: 2020, endYear: 0), // endYear=0 com year>0 = período aberto ("Atual")
        };
        var withYearInRange = CertificateMatcher.RankedMatches(
            "Universidade Federal do Acre Professor Assistente", "cert", new HashSet<int> { 2023 },
            entries, new HashSet<string>(), NoIdf, new HashSet<string>());
        var withYearFarBefore = CertificateMatcher.RankedMatches(
            "Universidade Federal do Acre Professor Assistente", "cert", new HashSet<int> { 2010 },
            entries, new HashSet<string>(), NoIdf, new HashSet<string>());

        Assert.NotEmpty(withYearInRange);
        Assert.NotEmpty(withYearFarBefore);
        Assert.True(withYearInRange[0].Score > withYearFarBefore[0].Score);
    }

    [Fact]
    public void GlobalAssignPrefereEntradaAindaDescobertaQuandoTop1EstaSaturado()
    {
        // 3 certificados, todos com top-1 apontando pro mesmo índice (0) com score quase
        // igual ao índice 1. A cobertura de cada índice é somada de TODOS os itens antes
        // de qualquer decisão (coverage[0]=3 já na primeira passada) — então o PRIMEIRO
        // item avaliado já vê coverage[0]>=2 && coverage[1]==0 e espalha para o índice 1;
        // os seguintes já encontram coverage[1]==1 (não mais 0) e permanecem no índice 0.
        var items = new List<CertificateMatcher.RankedItem>
        {
            new(new List<CertificateMatcher.ScoredMatch> { new(0, 0.80), new(1, 0.75) }),
            new(new List<CertificateMatcher.ScoredMatch> { new(0, 0.80), new(1, 0.75) }),
            new(new List<CertificateMatcher.ScoredMatch> { new(0, 0.80), new(1, 0.75) }),
        };
        var chosen = CertificateMatcher.GlobalAssign(items, guessFloor: 0.35);

        Assert.Equal(1, chosen[0]); // primeiro espalha para o índice 1 (top-2)
        Assert.Equal(0, chosen[1]); // demais ficam no top-1 (índice 0)
        Assert.Equal(0, chosen[2]);
    }

    [Fact]
    public void InferFolderKindsReconheceNomesDePastaComuns()
    {
        Assert.Contains("Banca", CertificateMatcher.InferFolderKinds("/Documentos/Bancas de Mestrado"));
        Assert.Contains("Formação", CertificateMatcher.InferFolderKinds("/Cursos/Alura/Certificados"));
        Assert.Contains("Orientação", CertificateMatcher.InferFolderKinds("/Orientações/TCCs"));
    }
}
