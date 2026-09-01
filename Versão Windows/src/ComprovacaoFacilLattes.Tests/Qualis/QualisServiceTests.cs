using ComprovacaoFacilLattes.Core.Qualis;

namespace ComprovacaoFacilLattes.Tests.Qualis;

public class QualisServiceTests
{
    private static QualisService NewLoadedService(string area = "FILOSOFIA")
    {
        var svc = new QualisService { Area = area };
        svc.Start();
        return svc;
    }

    [Fact]
    public void ClassificaPorIssnExatoNoQuadrienioCorreto()
    {
        var svc = NewLoadedService();
        // "A Cor das Letras UEFS", ISSN 1415-8973, FILOSOFIA, 2021-2024 = A3.
        var result = svc.Classify(venue: "", issn: "1415-8973", year: 2023);

        Assert.NotNull(result);
        Assert.Equal("A3", result!.Estrato);
        Assert.Equal("2021-2024", result.Quadriennium);
        Assert.Equal("FILOSOFIA", result.Area);
    }

    [Fact]
    public void ClassificaPorTituloExatoQuandoNaoHaIssn()
    {
        var svc = NewLoadedService();
        var result = svc.Classify(venue: "A Cor das Letras UEFS", issn: "", year: 2023);

        Assert.NotNull(result);
        Assert.Equal("A3", result!.Estrato);
    }

    [Fact]
    public void EscolheOQuadrienioPeloAnoDePublicacao()
    {
        var svc = NewLoadedService();
        // Mesmo ISSN pode não existir (ou ter estrato diferente) em quadriênios distintos —
        // aqui só validamos que o rótulo do quadriênio muda conforme o ano.
        var recent = svc.Classify(venue: "", issn: "1415-8973", year: 2024);
        var old = svc.Classify(venue: "", issn: "1415-8973", year: 2016);

        Assert.Equal("2021-2024", recent!.Quadriennium);
        if (old is not null) Assert.Equal("2016-2019", old.Quadriennium);
    }

    [Fact]
    public void IssnDesconhecidoETituloSemSobreposicaoNaoClassificam()
    {
        var svc = NewLoadedService();
        var result = svc.Classify(venue: "Revista Completamente Inexistente Zzzzz", issn: "0000-0000", year: 2023);

        Assert.Null(result);
    }

    [Fact]
    public void CarregaAListaDeAreasDisponiveis()
    {
        var svc = NewLoadedService();
        Assert.Contains("FILOSOFIA", svc.AllAreas);
        Assert.True(svc.AllAreas.Count > 10, "esperava dezenas de áreas de avaliação CAPES");
    }

    [Theory]
    [InlineData(2016, "2016_2019")]
    // 2017-2020 (>= 2017 já cai no quadriênio seguinte, mesmo dentro do intervalo "2016-2019").
    [InlineData(2019, "2017_2020")]
    [InlineData(2020, "2017_2020")]
    [InlineData(2021, "2021_2024")]
    [InlineData(2026, "2021_2024")]
    [InlineData(0, "2021_2024")]
    public void QuadKeyEscolheOQuadrienioCorreto(int year, string expected)
    {
        Assert.Equal(expected, QualisService.QuadKey(year));
    }
}
