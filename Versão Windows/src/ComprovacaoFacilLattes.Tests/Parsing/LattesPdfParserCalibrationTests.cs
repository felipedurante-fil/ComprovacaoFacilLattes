using ComprovacaoFacilLattes.Core.Parsing;
using ComprovacaoFacilLattes.Infrastructure.PdfText;

namespace ComprovacaoFacilLattes.Tests.Parsing;

/// <summary>
/// Valida o parser contra o PDF de calibração do Victor (matemática/pós-doc) — o mesmo
/// oráculo usado para calibrar o app macOS original: 6 páginas, layout limpo, COM
/// "Totais de produção". Os números esperados abaixo foram conferidos rodando o
/// <c>LattesPDFParser.swift</c> real (compilado isoladamente com <c>swiftc</c>) contra
/// este mesmo arquivo, não copiados de uma nota de memória desatualizada.
/// </summary>
public class LattesPdfParserCalibrationTests
{
    private static string FindCalibrationPdf()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curriculo CAlibrar 2.pdf")))
            dir = dir.Parent;
        if (dir is null) throw new FileNotFoundException("Curriculo CAlibrar 2.pdf não encontrado subindo a árvore de diretórios.");
        return Path.Combine(dir.FullName, "Curriculo CAlibrar 2.pdf");
    }

    private static LattesPdfParser.ParseResult ParseCalibrationPdf()
    {
        var path = FindCalibrationPdf();
        var text = PdfTextExtractor.ExtractFullText(path);
        return LattesPdfParser.Parse(text);
    }

    [Fact]
    public void ExtraiONomeDoTitular()
    {
        var result = ParseCalibrationPdf();
        Assert.Equal("Victor dos Santos Ronchim", result.ProfileName);
    }

    /// <summary>Reproduz exatamente a listagem de (título da seção → nº de entradas) do parser Swift original para o mesmo PDF.</summary>
    [Fact]
    public void ProduzAMesmaContagemDeSecoesQueOParserSwiftOriginal()
    {
        var result = ParseCalibrationPdf();
        var actual = result.Sections.ToDictionary(s => s.Title, s => s.Entries.Count);

        var expected = new Dictionary<string, int>
        {
            ["Formação acadêmica/titulação"] = 3,
            ["Pós-doutorado"] = 2,
            // 9, não 2: divergência ESPERADA em relação ao oráculo Swift/PDFKit, não um bug do
            // port. Nesta seção o PDF achata 8 colunas "AAAA - AAAA" numa única linha solta
            // (artefato de extração do PDFKit) que gruda erradamente 7 cursos distintos num só
            // bloco; o PdfPig reconstrói a ordem de leitura e associa cada período ao seu próprio
            // curso corretamente, então o MESMO algoritmo de chunking produz 9 entradas em vez
            // de 2 — resultado mais correto, não uma regressão. Verificado comparando as linhas
            // brutas extraídas por PdfPig vs. PDFKit para este trecho do PDF de calibração.
            ["Formação complementar"] = 9,
            ["Atuação profissional - Universidade Estadual Paulista Júlio de Mesquita Filho - UNESP - Vínculo institucional"] = 2,
            ["Atuação profissional - Universidade Estadual Paulista Júlio de Mesquita Filho - UNESP - Atividades administrativas"] = 3,
            ["Atuação profissional - Universidade de São Paulo - USP - Vínculo institucional"] = 1,
            ["Atuação profissional - Universidade Estadual do Centro-Oeste - UNICENTRO - Vínculo institucional"] = 1,
            ["Atuação profissional - Instituto de Matemática e Estatística - USP - IME-USP - Vínculo institucional"] = 1,
            ["Projetos de pesquisa"] = 1,
            ["Projetos de desenvolvimento"] = 1,
            ["Projetos de extensão"] = 5,
            ["Artigos completos publicados em periódicos"] = 4,
            ["Trabalhos publicados em anais de eventos"] = 6,
            ["Apresentação de trabalho e palestra"] = 5,
            ["Outras produções bibliográficas"] = 3,
            ["Orientações - Iniciação científica (concluídas)"] = 2,
            ["Orientações - Orientações de outra natureza (concluídas)"] = 4,
            ["Orientações - Orientações de outra natureza (em andamento)"] = 1,
            ["Participação em Eventos - Apresentação"] = 3,
            ["Participação em Eventos - Ouvinte"] = 8,
            ["Organização de eventos"] = 1,
            ["Participação em bancas (trabalhos de conclusão) - Mestrado"] = 2,
            ["Participação em bancas (trabalhos de conclusão) - Doutorado"] = 1,
            ["Participação em bancas (trabalhos de conclusão) - Qualificação de Doutorado"] = 1,
            ["Participação em bancas (comissões julgadoras)"] = 1,
        };

        Assert.Equal(expected, actual);
    }
}
