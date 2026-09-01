using System.Text;
using System.Text.RegularExpressions;
using ComprovacaoFacilLattes.Core.Matching;
using ComprovacaoFacilLattes.Core.Text;

namespace ComprovacaoFacilLattes.Core.Parsing;

/// <summary>
/// Converte o texto extraído do PDF do Lattes em seções e entradas estruturadas.
/// Calibrado para o layout real do currículo (CNPq), incluindo:
///  • truncamento no resumo "Totais de produção";
///  • colunas de numeração achatadas ("1. 2. 3. … N. conteúdo");
///  • hierarquia concluídas / em andamento;
///  • Atuação profissional (vínculos + disciplinas ministradas);
///  • remoção de rodapés (URL/paginação) que poluem o texto.
///
/// Adaptação do port: recebe o texto já extraído (<see cref="Parse"/>) em vez de um
/// arquivo — a extração de texto do PDF em si (PdfPig) fica na camada Infrastructure,
/// mantendo esta classe 100% portável/testável sem dependências de plataforma.
/// </summary>
public static class LattesPdfParser
{
    public sealed class ParseResult
    {
        public string ProfileName { get; init; } = "";
        public List<(string Title, List<ParsedEntry> Entries)> Sections { get; init; } = new();
        public string RawText { get; init; } = "";
    }

    public sealed class ParsedEntry
    {
        public string RawText;
        public string Title;
        public string Kind;
        public int Year;
        public string Authors;
        public string Venue;
        public string Doi;
        public string Isbn;
        public int Order;
        public string Portaria;
        public string Issn;
        public string Edital;
        public int EndYear;

        public ParsedEntry(
            string rawText, string title, string kind, int year,
            string authors, string venue, string doi, string isbn, int order,
            string portaria = "", string issn = "", string edital = "", int endYear = 0)
        {
            RawText = rawText; Title = title; Kind = kind; Year = year;
            Authors = authors; Venue = venue; Doi = doi; Isbn = isbn; Order = order;
            Portaria = portaria; Issn = issn; Edital = edital; EndYear = endYear;
        }
    }

    // MARK: - Entrada principal

    public static ParseResult Parse(string fullText)
    {
        var name = ExtractName(fullText);
        var sections = BuildSections(fullText);

        // Remove entradas-lixo (fragmentos sem conteúdo distintivo) que poluiriam
        // a indexação — ex.: "(Carga horária: 20h).", "01 12 2023.", "DURANTE, F.. Ep."
        var ownerStop = SimilarityMatcher.Normalize(name)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4).ToHashSet();

        var filtered = new List<(string Title, List<ParsedEntry> Entries)>();
        foreach (var sec in sections)
        {
            var kept = sec.Entries.Where(e => !IsJunkEntry(e, ownerStop)).ToList();
            if (kept.Count > 0) filtered.Add((sec.Title, kept));
        }

        return new ParseResult { ProfileName = name, Sections = filtered, RawText = fullText };
    }

    private static readonly HashSet<string> AdminWords = new()
    {
        "carga", "horaria", "hora", "horas", "episodio", "vol", "num", "pagina",
        "paginas", "total", "certificado", "certificamos", "declaracao", "declaramos",
        "outras", "informacoes", "nivel", "regime", "atual",
    };

    /// <summary>Uma entrada é "lixo" se, descontados nome do dono, termos administrativos e números, não sobra nenhuma palavra capaz de identificá-la.</summary>
    private static bool IsJunkEntry(ParsedEntry e, HashSet<string> ownerStop)
    {
        var toks = SimilarityMatcher.Normalize($"{e.Title} {e.Venue}")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 4 && !AdminWords.Contains(t) && !ownerStop.Contains(t) && !t.All(char.IsDigit));
        return !toks.Any();
    }

    // MARK: - Nome

    private static string ExtractName(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).ToList();
        // 1. O nome aparece imediatamente acima de "Endereço para acessar este CV".
        var idx = lines.FindIndex(l => l.Contains("Endereço para acessar este CV"));
        if (idx >= 0)
        {
            var j = idx - 1;
            while (j >= 0)
            {
                if (LooksLikeName(lines[j])) return lines[j];
                if (lines[j].Length > 0) break; // linha não-vazia que não é nome → para
                j--;
            }
        }
        // 2. Linha "Nome <Fulano>" da Identificação
        foreach (var t in lines.Where(l => l.StartsWith("Nome ")))
        {
            var rest = t[5..].Trim();
            if (rest.Length >= 3 && rest.Length < 70
                && !rest.ToLowerInvariant().StartsWith("em ")
                && !rest.ToLowerInvariant().Contains("citaç"))
            {
                return rest;
            }
        }
        // 3. Fallback: primeira linha que pareça um nome
        var fallback = lines.FirstOrDefault(LooksLikeName);
        return fallback ?? "Currículo Lattes";
    }

    /// <summary>Heurística para reconhecer uma linha que é um nome de pessoa (e não cabeçalho, URL, frase do topo "… anotou o Qualis …", etc.).</summary>
    private static bool LooksLikeName(string t)
    {
        if (t.Length < 5 || t.Length > 70) return false;
        if (t.Contains("http") || t.Contains(':') || t.Contains('@') || t.Contains('/')) return false;
        if (t.Any(char.IsDigit)) return false;
        var low = t.ToLowerInvariant();
        foreach (var bad in new[] { "curríc", "curric", "anotou", "ver artigos", "visualizar", "endereço" })
            if (low.Contains(bad)) return false;
        return t.Split(' ').Count(w => w.Length >= 2) >= 2;
    }

    // MARK: - Classificação de cabeçalhos

    private enum HeaderKind { Stop, Excluded, GroupConcluidas, GroupAndamento, Section }

    private enum Special { None, Atuacao, Banca, Projetos, Organizacao, Eventos, Premios, Formacao }

    private sealed class HeaderClass
    {
        public HeaderKind Kind { get; private init; }
        public string Display { get; private init; } = "";
        public Special Special { get; private init; }
        public bool IsChild { get; private init; }

        public static readonly HeaderClass StopInstance = new() { Kind = HeaderKind.Stop };
        public static readonly HeaderClass ExcludedInstance = new() { Kind = HeaderKind.Excluded };
        public static readonly HeaderClass GroupConcluidasInstance = new() { Kind = HeaderKind.GroupConcluidas };
        public static readonly HeaderClass GroupAndamentoInstance = new() { Kind = HeaderKind.GroupAndamento };

        public static HeaderClass SectionOf(string display, Special special, bool isChild) =>
            new() { Kind = HeaderKind.Section, Display = display, Special = special, IsChild = isChild };
    }

    /// <summary>Cabeçalhos de seções que CONTÊM entradas (alias normalizado → exibição).</summary>
    private static readonly (string Alias, string Display, Special Special, bool IsChild)[] SectionTable =
    {
        ("formacao academica/titulacao", "Formação acadêmica/titulação", Special.Formacao, false),
        ("pos-doutorado", "Pós-doutorado", Special.Formacao, false),
        ("formacao complementar", "Formação complementar", Special.None, false),
        ("premios e titulos", "Prêmios e títulos", Special.Premios, false),
        ("atuacao profissional", "Atuação profissional", Special.Atuacao, false),
        ("projetos de pesquisa", "Projetos de pesquisa", Special.Projetos, false),
        ("projeto de pesquisa", "Projetos de pesquisa", Special.Projetos, false),
        ("projetos de extensao", "Projetos de extensão", Special.Projetos, false),
        ("projeto de extensao", "Projetos de extensão", Special.Projetos, false),
        ("projetos de desenvolvimento", "Projetos de desenvolvimento", Special.Projetos, false),
        ("membro de corpo editorial", "Membro de corpo editorial", Special.None, false),
        ("artigos completos publicados em periodicos", "Artigos completos publicados em periódicos", Special.None, false),
        ("artigos aceitos para publicacao", "Artigos aceitos para publicação", Special.None, false),
        ("livros publicados", "Livros publicados", Special.None, false),
        ("capitulos de livros publicados", "Capítulos de livros publicados", Special.None, false),
        ("trabalhos publicados em anais de eventos", "Trabalhos publicados em anais de eventos", Special.None, false),
        ("trabalhos completos publicados em anais de congressos", "Trabalhos completos publicados em anais", Special.None, false),
        ("resumos expandidos publicados em anais de congressos", "Resumos expandidos publicados em anais", Special.None, false),
        ("resumos publicados em anais de congressos", "Resumos publicados em anais", Special.None, false),
        ("textos em jornais de noticias/revistas", "Textos em jornais de notícias/revistas", Special.None, false),
        ("apresentacao de trabalho e palestra", "Apresentação de trabalho e palestra", Special.None, false),
        ("apresentacoes de trabalho", "Apresentação de trabalho e palestra", Special.None, false),
        ("apresentacao de trabalho", "Apresentação de trabalho e palestra", Special.None, false),
        ("outras producoes bibliograficas", "Outras produções bibliográficas", Special.None, false),
        ("trabalhos tecnicos", "Trabalhos técnicos", Special.None, false),
        ("demais tipos de producao tecnica", "Demais produções técnicas", Special.None, false),
        ("entrevistas, mesas redondas, programas e comentarios na midia",
         "Entrevistas, mesas redondas e programas na mídia", Special.None, false),
        ("demais producoes tecnicas", "Demais produções técnicas", Special.None, false),
        ("produtos tecnicos", "Produtos técnicos", Special.None, false),
        ("programas de computador", "Programas de computador", Special.None, false),
        ("participacao em eventos", "Participação em eventos", Special.Eventos, false),
        ("organizacao de evento", "Organização de eventos", Special.Organizacao, false),
        ("participacao em banca de trabalhos de conclusao", "Participação em bancas (trabalhos de conclusão)", Special.Banca, false),
        ("participacao em bancas de trabalhos de conclusao", "Participação em bancas (trabalhos de conclusão)", Special.Banca, false),
        ("participacao em banca de comissoes julgadoras", "Participação em bancas (comissões julgadoras)", Special.Banca, false),
        ("participacao em bancas de comissoes julgadoras", "Participação em bancas (comissões julgadoras)", Special.Banca, false),
        // Filhos de Orientações (recebem sufixo concluídas / em andamento)
        ("dissertacoes de mestrado: orientador principal", "Dissertações de mestrado", Special.None, true),
        ("dissertacoes de mestrado: coorientador", "Dissertações de mestrado (coorientação)", Special.None, true),
        ("teses de doutorado: orientador principal", "Teses de doutorado", Special.None, true),
        ("teses de doutorado: coorientador", "Teses de doutorado (coorientação)", Special.None, true),
        ("iniciacao cientifica", "Iniciação científica", Special.None, true),
        ("orientacao de outra natureza", "Orientações de outra natureza", Special.None, true),
        ("trabalho de conclusao de curso de graduacao", "TCC de graduação", Special.None, true),
        ("supervisao de pos-doutorado", "Supervisão de pós-doutorado", Special.None, true),
    };

    /// <summary>Cabeçalhos que delimitam, mas NÃO geram seções com comprovação.</summary>
    private static readonly HashSet<string> ExcludedHeaders = new()
    {
        "identificacao", "idiomas", "areas de atuacao", "endereco",
        "linhas de pesquisa", "linha de pesquisa",
        "producao", "producao bibliografica", "producao tecnica",
        "orientacoes e supervisoes", "orientacoes e supervisoes",
        "eventos", "bancas", "educacao e popularizacao de c&t",
        "outras informacoes relevantes", "dados complementares",
    };

    private static HeaderClass? Classify(string norm)
    {
        if (norm == "totais de producao") return HeaderClass.StopInstance;
        if (ExcludedHeaders.Contains(norm)) return HeaderClass.ExcludedInstance;
        if (norm == "orientacoes e supervisoes concluidas") return HeaderClass.GroupConcluidasInstance;
        if (norm == "orientacoes e supervisoes em andamento") return HeaderClass.GroupAndamentoInstance;
        foreach (var row in SectionTable)
        {
            if (norm == row.Alias) return HeaderClass.SectionOf(row.Display, row.Special, row.IsChild);
            // Prefix match (títulos partidos em duas linhas, ou com qualificador como
            // "(completo)") — mas nunca quando a linha tem um ")" sem "(" correspondente:
            // aí é o fecho de uma anotação de entrada que por coincidência começa com as
            // mesmas palavras do título da seção, não um cabeçalho de verdade — um
            // cabeçalho real nunca tem parênteses desbalanceados.
            if (row.Alias.Length >= 12 && norm.StartsWith(row.Alias) && !HasUnbalancedClosingParen(norm))
                return HeaderClass.SectionOf(row.Display, row.Special, row.IsChild);
        }
        return null;
    }

    /// <summary>Verdadeiro quando a linha tem mais ")" do que "(" — sinal de que ela é o FECHO de um parêntese aberto numa linha anterior (anotação de entrada quebrada pela paginação), não um título/cabeçalho de verdade.</summary>
    private static bool HasUnbalancedClosingParen(string s) =>
        s.Count(c => c == ')') > s.Count(c => c == '(');

    // MARK: - Construção das seções

    private sealed class RawSection
    {
        public string Title = "";
        public Special Special;
        public string Body = "";
    }

    private static List<(string Title, List<ParsedEntry> Entries)> BuildSections(string text)
    {
        var raws = new List<RawSection>();
        RawSection? current = null;
        var parentSuffix = "";

        void Flush()
        {
            if (current is { } c)
            {
                c.Body = c.Body.Trim();
                if (c.Body.Length > 0) raws.Add(c);
                current = null;
            }
        }

        var lines = text.Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            if (IsNoise(line)) { i++; continue; }
            var norm = NormalizeHeader(line);
            if (norm.Length == 0)
            {
                if (current is not null) current.Body += "\n";
                i++; continue;
            }

            // Classifica a linha; se não for cabeçalho, tenta juntá-la com a próxima —
            // o Lattes às vezes quebra cabeçalhos em duas linhas ("Projetos de"/"pesquisa").
            var hc = Classify(norm);
            var consumed = 1;
            if (hc is null && i + 1 < lines.Length && !HasUnbalancedClosingParen(norm))
            {
                var next = lines[i + 1].Trim();
                if (next.Length > 0 && !IsNoise(next))
                {
                    var c2 = Classify(NormalizeHeader(line + " " + next));
                    if (c2 is { Kind: HeaderKind.Section or HeaderKind.Excluded })
                    {
                        hc = c2; consumed = 2;
                    }
                }
            }

            switch (hc?.Kind)
            {
                case HeaderKind.Stop:
                    Flush();
                    return Finalize(raws);
                case HeaderKind.Excluded:
                    Flush(); parentSuffix = "";
                    break;
                case HeaderKind.GroupConcluidas:
                    Flush(); parentSuffix = " (concluídas)";
                    break;
                case HeaderKind.GroupAndamento:
                    Flush(); parentSuffix = " (em andamento)";
                    break;
                case HeaderKind.Section:
                    Flush();
                    var title = hc!.IsChild ? $"Orientações - {hc.Display}{parentSuffix}" : hc.Display;
                    if (!hc.IsChild) parentSuffix = "";
                    current = new RawSection { Title = title, Special = hc.Special, Body = "" };
                    break;
                default: // linha não é cabeçalho → pertence ao corpo da seção atual
                    if (current is not null) current.Body += line + "\n";
                    break;
            }
            i += consumed;
        }
        Flush();
        return Finalize(raws);
    }

    private static List<(string Title, List<ParsedEntry> Entries)> Finalize(List<RawSection> raws)
    {
        // Agrupa seções de mesmo título (cabeçalho repetido em páginas/áreas diferentes)
        // mas parseia cada corpo SEPARADAMENTE: as listagens duplicadas costumam vir com
        // layout diferente (achatado) e, concatenadas, contaminariam a detecção de modo
        // da listagem principal. O Append deduplica entre os corpos.
        var order = new List<string>();
        var grouped = new Dictionary<string, (Special Special, List<string> Bodies)>();
        foreach (var raw in raws)
        {
            if (grouped.TryGetValue(raw.Title, out var existing))
            {
                existing.Bodies.Add(raw.Body);
            }
            else
            {
                grouped[raw.Title] = (raw.Special, new List<string> { raw.Body });
                order.Add(raw.Title);
            }
        }

        var result = new List<(string Title, List<ParsedEntry> Entries)>();
        foreach (var title in order)
        {
            var info = grouped[title];
            foreach (var body in info.Bodies)
            {
                switch (info.Special)
                {
                    case Special.Atuacao:
                        // Separa por vínculo (instituição) e, dentro dele, por categoria —
                        // ordenado do mais recente ao mais antigo.
                        foreach (var (label, ents) in GroupAtuacaoPorVinculo(ParseAtuacao(body)))
                            Append(result, $"{title} - {label}", ents);
                        break;
                    case Special.Banca:
                        // "Trabalhos de conclusão" costuma vir subdividido por nível —
                        // separa em seções próprias. "Comissões julgadoras" não tem
                        // esses marcadores.
                        if (title.Contains("trabalhos de conclusão"))
                        {
                            foreach (var (nivel, ents) in ParseBancaPorNivel(body))
                                Append(result, nivel.Length == 0 ? title : $"{title} - {nivel}", ents);
                        }
                        else
                        {
                            Append(result, title, ParseEntries(body, "Banca", banca: true));
                        }
                        break;
                    case Special.Projetos:
                        Append(result, title, ParseProjetos(body, "Projeto"));
                        break;
                    case Special.Organizacao:
                        Append(result, title, ParseOrganizacao(body));
                        break;
                    case Special.Premios:
                        Append(result, title, ParsePremios(body));
                        break;
                    case Special.Formacao:
                        Append(result, title, ParseFormacao(body));
                        break;
                    case Special.Eventos:
                        // Distingue apresentação (apresentou trabalho) de ouvinte (só participou)
                        var all = ParseEntries(body, "Evento", banca: false);
                        Append(result, "Participação em Eventos - Apresentação", all.Where(e => !IsOuvinte(e)).ToList());
                        Append(result, "Participação em Eventos - Ouvinte", all.Where(IsOuvinte).ToList());
                        break;
                    default:
                        Append(result, title, ParseEntries(body, KindLabel(title), banca: false));
                        break;
                }
            }
        }
        return result;
    }

    private static void Append(List<(string Title, List<ParsedEntry> Entries)> list, string title, List<ParsedEntry> entries)
    {
        if (entries.Count == 0) return;
        var idx = list.FindIndex(x => x.Title == title);
        if (idx < 0)
        {
            var deduped = DedupeEntries(entries);
            if (deduped.Count > 0) list.Add((title, deduped));
            return;
        }

        // Título já existe (listagem duplicada em outra área): funde, descartando
        // candidatos que dupliquem entradas existentes — mesmo com cauda diferente
        // (URL/metadados) ou fragmentos compostos que ENGOLIRAM uma entrada real. O
        // sinal é o prefixo normalizado longo (>=60) de um aparecer dentro do outro.
        var merged = list[idx].Entries;
        var existingNorms = merged.Select(m => SimilarityMatcher.Normalize(m.RawText)).ToList();
        var existingTitles = merged.Select(m => SimilarityMatcher.Normalize(m.Title)).ToList();
        foreach (var e in entries)
        {
            var n = SimilarityMatcher.Normalize(e.RawText);
            var nTitle = SimilarityMatcher.Normalize(e.Title);
            var isDup = false;
            for (var k = 0; k < existingNorms.Count; k++)
            {
                var ex = existingNorms[k];
                if (ex == n) { isDup = true; break; }
                // Prefixo longo compartilhado NÃO basta (entradas distintas repetem a
                // mesma lista de autores no início) — exige também que o TÍTULO de uma
                // apareça no texto da outra.
                var exPref = ex.Length >= 60 ? ex[..60] : ex;
                var nPref = n.Length >= 60 ? n[..60] : n;
                var prefixHit = (exPref.Length >= 60 && n.Contains(exPref)) || (nPref.Length >= 60 && ex.Contains(nPref));
                if (!prefixHit) continue;
                var exTitle = existingTitles[k].Length >= 30 ? existingTitles[k][..30] : existingTitles[k];
                var candTitle = nTitle.Length >= 30 ? nTitle[..30] : nTitle;
                if ((exTitle.Length >= 15 && n.Contains(exTitle)) || (candTitle.Length >= 15 && ex.Contains(candTitle)))
                {
                    isDup = true; break;
                }
            }
            if (!isDup) merged.Add(e);
        }
        merged = DedupeEntries(merged);
        for (var k = 0; k < merged.Count; k++) merged[k].Order = k;
        list[idx] = (list[idx].Title, merged);
    }

    /// <summary>Papéis que indicam participação ATIVA (apresentou/conduziu algo).</summary>
    private static readonly string[] PresentationRoles =
    {
        "conferencista", "apresentacao", "comunicacao", "moderador", "mediador",
        "palestrante", "debatedor", "expositor", "avaliador", "coordenador",
        "organizador", "relator", "painelista", "entrevistado", "instrutor",
    };

    private static readonly Regex LeadingNumberClusterRegex = new(@"^\s*(\d{1,3}\.\s*)+", RegexOptions.Compiled);

    /// <summary>Uma participação é "ouvinte" quando NÃO há papel de apresentação — a entrada começa direto pelo nome do evento em vez de "Conferencista no(a)…".</summary>
    private static bool IsOuvinte(ParsedEntry e)
    {
        var n = LeadingNumberClusterRegex.Replace(NormalizeHeader(e.RawText), "");
        if (n.StartsWith("ouvinte") || n.Contains("(ouvinte)")) return true;
        foreach (var role in PresentationRoles) if (n.StartsWith(role)) return false;
        return true; // começa pelo nome do evento → ouvinte
    }

    /// <summary>Remove entradas idênticas que surgem da mescla de seções repetidas.</summary>
    private static List<ParsedEntry> DedupeEntries(List<ParsedEntry> entries)
    {
        // 1) Remove duplicatas exatas pelo texto bruto normalizado.
        var seen = new HashSet<string>();
        var result = new List<ParsedEntry>();
        foreach (var e in entries)
        {
            var key = $"{e.Year}|{SimilarityMatcher.Normalize(e.RawText)}";
            if (seen.Add(key)) result.Add(e);
        }

        // 2) Remove versões TRUNCADAS: uma entrada que TERMINA no ano e é prefixo de
        // outra completa do mesmo ano. A exigência de terminar no ano evita remover
        // eventos distintos.
        var norm = result.Select(e => SimilarityMatcher.Normalize(e.RawText)).ToList();
        var keep = Enumerable.Repeat(true, result.Count).ToArray();
        var yearSuffixRegex = new Regex(@"(19|20)\d{2}$");
        for (var i = 0; i < result.Count; i++)
        {
            if (norm[i].Length < 30 || !yearSuffixRegex.IsMatch(norm[i])) continue;
            for (var j = 0; j < result.Count; j++)
            {
                if (i == j) continue;
                if (result[i].Year == result[j].Year && norm[j].Length > norm[i].Length + 4 && norm[j].StartsWith(norm[i]))
                {
                    keep[i] = false; break;
                }
            }
        }
        return result.Where((_, idx) => keep[idx]).ToList();
    }

    // MARK: - Rótulo curto por tipo de seção

    public static string KindLabel(string title)
    {
        var c = NormalizeHeader(title);
        if (c.Contains("artigo")) return "Artigo";
        if (c.Contains("livro") || c.Contains("capitulo")) return "Livro/Capítulo";
        if (c.Contains("anais")) return "Trabalho em evento";
        if (c.Contains("banca")) return "Banca";
        if (c.Contains("dissertacoes") || c.Contains("teses") || c.Contains("iniciacao")
            || c.Contains("orientac") || c.Contains("tcc") || c.Contains("supervisao")) return "Orientação";
        if (c.Contains("apresentac")) return "Apresentação";
        if (c.Contains("participacao em eventos")) return "Evento";
        if (c.Contains("organizacao de evento")) return "Organização de evento";
        if (c.Contains("projeto")) return "Projeto";
        if (c.Contains("premio") || c.Contains("titulo")) return "Prêmio/Título";
        if (c.Contains("formacao") || c.Contains("doutorado")) return "Formação";
        if (c.Contains("corpo editorial")) return "Corpo editorial";
        if (c.Contains("entrevista")) return "Mídia";
        if (c.Contains("tecnic") || c.Contains("produto")) return "Produção técnica";
        return "";
    }

    // MARK: - Atuação profissional (vínculos + disciplinas)

    private static readonly Regex VinculoRegex = new(@"^\d{4}\s*-\s*(\d{4}|Atual)\s+Vínculo:", RegexOptions.Compiled);

    private static List<ParsedEntry> ParseAtuacao(string body)
    {
        var lines = body.Split('\n').Select(l => l.Trim()).Where(l => !IsNoise(l) && l.Length > 0).ToList();

        var entries = new List<ParsedEntry>();
        var order = 0;
        var institution = "";
        var lastPeriodYear = 0;      // ano "rótulo" (fim do período) — usado pelas disciplinas
        var lastPeriodStartYear = 0; // início do período — usado pelas atividades
        var lastPeriodEndYear = 0;   // fim do período (0 = "Atual"/aberto)
        var lastVinculoIdx = -1;

        bool IsInstitution(string l)
        {
            if (Regex.IsMatch(l, @"^\d")) return false;
            var low = NormalizeHeader(l);
            // Evita falsos positivos: PORTARIA, continuação de "Regime:", etc.
            if (low.Contains("portaria") || low.Contains("lotado")
                || low.Contains("outras informac") || low.Contains("vinculo:")
                || low.Contains("dedicac") || low.Contains("regime")
                || low.Contains("carga hor")) return false;
            return low.Contains("universidade") || low.Contains("instituto")
                || low.Contains("faculdade") || low.Contains("fundacao");
        }

        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            var norm = NormalizeHeader(line);

            if (IsInstitution(line))
            {
                institution = Regex.Replace(
                        line.Replace(", Brasil.", "").Replace(", Brasil", ""),
                        @"\s*(Integral|Parcial|Horista|Dedica[çc][ãa]o\s*[Ee]xclusiva)\s*$", "")
                    .Trim();
                lastVinculoIdx = -1;
                i++; continue;
            }

            // Após "Atividades", as portarias pertencem a atividades, não ao vínculo
            if (norm == "atividades") { lastVinculoIdx = -1; i++; continue; }

            // Vínculo institucional
            if (VinculoRegex.IsMatch(line))
            {
                var enquad = FirstMatch(line, @"Enquadramento funcional:\s*([^,]+)").Trim();
                var period = FirstMatch(line, @"^(\d{4}\s*-\s*(?:\d{4}|Atual))");
                var startY = int.TryParse(FirstMatch(period, @"^(\d{4})"), out var sy) ? sy : 0;
                var endY = int.TryParse(FirstMatch(period, @"-\s*(\d{4})"), out var ey) ? ey : 0;
                var titleCore = enquad.Length == 0 ? "Vínculo institucional" : enquad;
                entries.Add(new ParsedEntry(
                    rawText: line,
                    title: institution.Length == 0 ? titleCore : $"{titleCore} — {institution}",
                    kind: "Vínculo institucional",
                    year: startY > 0 ? startY : ExtractYear(period),
                    authors: "", venue: institution,
                    doi: "", isbn: "", order: order,
                    portaria: string.Join(" ", SimilarityMatcher.PortariaPairs(line)),
                    endYear: endY));
                lastVinculoIdx = entries.Count - 1;
                order++;
                i++; continue;
            }

            // Portaria (geralmente em "Outras informações:") → associa ao último vínculo
            if (norm.Contains("portaria") && lastVinculoIdx >= 0)
            {
                var nums = SimilarityMatcher.PortariaPairs(line);
                if (nums.Count > 0)
                {
                    var existing = entries[lastVinculoIdx].Portaria
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    existing.UnionWith(nums);
                    entries[lastVinculoIdx].Portaria = string.Join(" ", existing);
                }
            }

            // Linha de período de atividade (ex.: "08/2019 - 12/2019 Graduação, Filosofia")
            if (Regex.IsMatch(line, @"^\d{1,2}/\d{4}"))
            {
                var head20 = line.Length > 20 ? line[..20] : line;
                var y = ExtractYear(head20);
                if (y > 0) lastPeriodYear = y;
                lastPeriodStartYear = int.TryParse(FirstMatch(head20, @"/(\d{4})"), out var psy) ? psy : y;
                var head25 = line.Length > 25 ? line[..25] : line;
                var head30 = line.Length > 30 ? line[..30] : line;
                lastPeriodEndYear = head25.ToLowerInvariant().Contains("atual")
                    ? 0
                    : (int.TryParse(FirstMatch(head30, @"-\s*\d{0,2}/?(\d{4})"), out var pey) ? pey : 0);
            }

            // Atividades administrativas: "Especificação:" (Conselhos, Comissões e
            // Consultoria) e "Cargos ocupados:" (Direção e Administração). O detalhe
            // (cargo/função, com portarias) vem nas linhas seguintes, até o próximo
            // período / cabeçalho.
            if (norm.StartsWith("especificacao") || norm.StartsWith("cargos ocupados"))
            {
                var detail = new List<string>();
                var j = i + 1;
                while (j < lines.Count)
                {
                    var l = lines[j];
                    var n = NormalizeHeader(l);
                    if (Regex.IsMatch(l, @"^\d{1,2}/\d{4}\s*-")) break;
                    if (IsInstitution(l) || n == "atividades" || l.StartsWith("https://")) break;
                    if (n.StartsWith("outras informacoes") || n.StartsWith("disciplinas ministradas")
                        || n.StartsWith("especificacao") || n.StartsWith("cargos ocupados")) break;
                    if (VinculoRegex.IsMatch(l)) break;
                    detail.Add(l);
                    j++;
                }
                var content = string.Join(" ", detail).Trim();
                var actTitle = ActivityTitle(content);
                if (actTitle.Length >= 3)
                {
                    entries.Add(new ParsedEntry(
                        rawText: content, title: actTitle,
                        kind: "Atividade administrativa",
                        year: lastPeriodStartYear > 0 ? lastPeriodStartYear : lastPeriodYear,
                        authors: "", venue: institution,
                        doi: "", isbn: "", order: order,
                        portaria: string.Join(" ", SimilarityMatcher.PortariaPairs(content)),
                        endYear: lastPeriodEndYear));
                    order++;
                }
                i = j; continue;
            }

            // Disciplinas ministradas
            if (norm.Contains("disciplinas ministradas"))
            {
                var disc = "";
                var colonIdx = line.IndexOf(':');
                if (colonIdx >= 0) disc = line[(colonIdx + 1)..].Trim();
                if (disc.Length == 0 && i + 1 < lines.Count) { disc = lines[i + 1]; i++; }
                disc = disc.Trim();
                if (disc.Length >= 3)
                {
                    var y = ExtractYear(disc);
                    entries.Add(new ParsedEntry(
                        rawText: disc, title: disc, kind: "Disciplina ministrada",
                        year: y > 0 ? y : lastPeriodYear,
                        authors: "", venue: institution,
                        doi: "", isbn: "", order: order));
                    order++;
                }
                i++; continue;
            }

            i++;
        }

        if (entries.Count == 0)
        {
            return new List<ParsedEntry>
            {
                new(rawText: body, title: institution.Length == 0 ? "Atuação profissional" : institution,
                    kind: "Vínculo institucional", year: 0, authors: "", venue: institution,
                    doi: "", isbn: "", order: 0)
            };
        }
        return entries;
    }

    /// <summary>
    /// Reorganiza a "Atuação profissional" por vínculo (instituição) e, dentro dele,
    /// por categoria (Vínculo institucional / Atividades administrativas / Disciplinas
    /// ministradas). Instituições e entradas dentro de cada grupo vêm da mais recente
    /// para a mais antiga (vínculo/atividade em aberto — "Atual" — conta como mais
    /// recente; disciplinas não têm período aberto, então usam só o próprio ano).
    /// </summary>
    private static List<(string Label, List<ParsedEntry> Entries)> GroupAtuacaoPorVinculo(List<ParsedEntry> flat)
    {
        if (flat.Count == 0) return new List<(string, List<ParsedEntry>)>();

        int Recency(ParsedEntry e)
        {
            if (e.Kind != "Disciplina ministrada" && e.EndYear == 0 && e.Year > 0) return 9999;
            return Math.Max(e.Year, e.EndYear);
        }

        // Chave de agrupamento normalizada: o mesmo vínculo pode aparecer com ou sem a
        // sigla no fim por causa do artefato de reordenação do Regime — sem isso, a
        // mesma instituição vira dois grupos separados.
        string InstitutionKey(string venue) =>
            NormalizeHeader(Regex.Replace(venue, @"\s*-\s*[A-ZÀ-Ú]{2,10}$", ""));

        var byInstitution = flat.GroupBy(e => e.Venue.Length == 0 ? "outros vinculos" : InstitutionKey(e.Venue))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Rótulo de exibição: a variante mais completa (mais longa) do nome da
        // instituição encontrada no grupo — normalmente a que TEM a sigla.
        var displayName = byInstitution.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(e => e.Venue).Where(v => v.Length > 0)
                .OrderByDescending(v => v.Length).FirstOrDefault() ?? "Outros vínculos");

        var institutionOrder = byInstitution.Keys.OrderByDescending(k => byInstitution[k].Max(Recency))
            .ThenBy(k => displayName[k], StringComparer.Ordinal).ToList();

        var kindOrder = new (string Kind, string Display)[]
        {
            ("Vínculo institucional", "Vínculo institucional"),
            ("Atividade administrativa", "Atividades administrativas"),
            ("Disciplina ministrada", "Disciplinas ministradas"),
        };

        var groups = new List<(string Label, List<ParsedEntry> Entries)>();
        foreach (var inst in institutionOrder)
        {
            var entriesForInst = byInstitution[inst];
            foreach (var (kind, display) in kindOrder)
            {
                var sub = entriesForInst.Where(e => e.Kind == kind).OrderByDescending(Recency).ToList();
                if (sub.Count == 0) continue;
                for (var k = 0; k < sub.Count; k++) sub[k].Order = k;
                groups.Add(($"{displayName[inst]} - {display}", sub));
            }
        }
        return groups;
    }

    /// <summary>Extrai um título curto do cargo/função de uma atividade administrativa, cortando no primeiro marcador de portaria/resolução ou separador de itens.</summary>
    private static string ActivityTitle(string content)
    {
        var s = content.Trim();
        // remove data inicial "DD/MM/AAAA - " (Cargos ocupados)
        s = Regex.Replace(s, @"^\d{1,2}/\d{1,2}/\d{4}\s*-\s*", "");
        // localiza o marcador mais próximo: portaria/resolução ou " , "
        var cut = s.Length;
        var m1 = Regex.Match(s, @"\s*[-,.]?\s*(?i:portaria|resolu[çc][ãa]o)\b");
        if (m1.Success && m1.Index < cut) cut = m1.Index;
        var idx2 = s.IndexOf(" , ", StringComparison.Ordinal);
        if (idx2 >= 0 && idx2 < cut) cut = idx2;
        s = s[..cut].Trim(' ', '-', ',', '.', ';');
        return s;
    }

    // MARK: - Projetos (pesquisa / extensão)

    /// <summary>
    /// Cada projeto tem um TÍTULO seguido de "Descrição:". Como a coluna de períodos
    /// costuma vir achatada (vários "AAAA - AAAA" numa linha), usamos a "Descrição:"
    /// como âncora: o título é a linha de conteúdo imediatamente anterior.
    /// </summary>
    private static List<ParsedEntry> ParseProjetos(string body, string kind)
    {
        var lines = body.Split('\n').Select(l => l.Trim()).Where(l => !IsNoise(l) && l.Length > 0).ToList();

        bool IsMeta(string l)
        {
            var n = NormalizeHeader(l);
            foreach (var p in new[]
                     {
                         "situacao", "natureza", "alunos", "integrantes", "financiador",
                         "descricao", "palavras", "numero de producoes", "numero de produc",
                         "coordenador", "membro",
                     })
                if (n.StartsWith(p)) return true;
            // Continuação de lista de integrantes (nomes separados por ";")
            if (l.Count(c => c == ';') >= 2) return true;
            // Linha composta só de períodos (e, eventualmente, "Situação…" ou "Número…")
            var noPeriods = Regex.Replace(l, @"\d{4}\s*-\s*(?:\d{4}|Atual)", "").Trim();
            if (noPeriods.Length == 0) return true;
            var nn = NormalizeHeader(noPeriods);
            if (nn.StartsWith("situacao") || nn.StartsWith("numero")) return true;
            return false;
        }

        // Para cada "Descrição:", o título é a linha de conteúdo anterior (juntando a
        // linha de cima se o título tiver quebrado e ficado curto).
        var projs = new List<(int Start, string Title)>();
        var seenStart = new HashSet<int>();
        for (var idx = 0; idx < lines.Count; idx++)
        {
            if (!NormalizeHeader(lines[idx]).StartsWith("descricao")) continue;
            var t = idx - 1;
            while (t >= 0 && IsMeta(lines[t])) t--;
            if (t < 0) continue;
            var titleLines = new List<string> { lines[t] };
            var start = t;
            if (lines[t].Length < 28 && t - 1 >= 0 && !IsMeta(lines[t - 1]))
            {
                titleLines.Insert(0, lines[t - 1]);
                start = t - 1;
            }
            if (!seenStart.Add(start)) continue;
            var title = Regex.Replace(string.Join(" ", titleLines),
                    @"^(\d{4}\s*-\s*(?:\d{4}|Atual)\s*)+", "")
                .Trim();
            if (title.Length < 3) title = string.Join(" ", titleLines);
            projs.Add((start, title));
        }
        projs.Sort((a, b) => a.Start.CompareTo(b.Start));

        if (projs.Count == 0)
        {
            // Sem âncoras "Descrição:". Se o corpo é conteúdo de Atuação profissional
            // que vazou, não há projetos aqui — descarta.
            var n = NormalizeHeader(body);
            if (n.Contains("disciplinas ministradas") || n.Contains("vinculo:")
                || n.Contains("conselhos, comissoes")) return new List<ParsedEntry>();
            return ParseEntries(body, kind, banca: false);
        }

        var entries = new List<ParsedEntry>();
        for (var k = 0; k < projs.Count; k++)
        {
            var p = projs[k];
            var end = k + 1 < projs.Count ? projs[k + 1].Start : lines.Count;
            var block = string.Join(" ", lines.Skip(p.Start).Take(end - p.Start));
            var year = ExtractYear(p.Title) > 0 ? ExtractYear(p.Title) : ExtractYear(block);
            entries.Add(new ParsedEntry(
                rawText: block, title: p.Title, kind: kind, year: year,
                authors: "", venue: "", doi: "", isbn: "", order: k));
        }
        return entries;
    }

    // MARK: - Organização de eventos

    private static readonly Regex EventoCloseRegex = new(@"evento\s*\)", RegexOptions.IgnoreCase);
    private static readonly Regex LeadingNumClusterStrict = new(@"^\s*(\d+\.\s*)+");
    private static readonly Regex MidNumClusterRegex = new(@"\s(\d+\.\s){2,}");

    /// <summary>Cada organização termina com "(…, Organização de evento)". Como a coluna de numeração às vezes vem achatada, dividimos no terminador "evento)".</summary>
    private static List<ParsedEntry> ParseOrganizacao(string body)
    {
        const string kind = "Organização de evento";
        var joined = string.Join(" ", body.Split('\n').Select(l => l.Trim()).Where(l => !IsNoise(l)));

        var matches = EventoCloseRegex.Matches(joined);
        if (matches.Count < 2) return ParseEntries(body, kind, banca: false);

        var entries = new List<ParsedEntry>();
        var start = 0;
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var end = m.Index + m.Length;
            var chunk = joined[start..end];
            chunk = LeadingNumClusterStrict.Replace(chunk, "").Trim();
            // remove cluster de números que tenha sobrado no meio (coluna achatada)
            chunk = MidNumClusterRegex.Replace(chunk, " ");
            start = end;
            if (chunk.Length < 8) continue;
            entries.Add(new ParsedEntry(
                rawText: chunk, title: OrgEventTitle(chunk), kind: kind,
                year: ExtractYear(chunk), authors: "", venue: "",
                doi: "", isbn: "", order: i));
        }
        return entries;
    }

    /// <summary>Extrai o nome do evento de uma entrada de organização: "AUTORES.. NOME DO EVENTO, AAAA. (Tipo, Organização de evento)".</summary>
    private static string OrgEventTitle(string chunk)
    {
        var s = chunk;
        // Após a lista de autores (termina com ".. ")
        var dotDotIdx = s.IndexOf(".. ", StringComparison.Ordinal);
        if (dotDotIdx >= 0) s = s[(dotDotIdx + 3)..];
        // Corta no ano de realização
        var yearMatch = Regex.Match(s, @",\s*(19|20)\d{2}");
        if (yearMatch.Success) s = s[..yearMatch.Index];
        s = s.Trim();
        return s.Length >= 3 ? s : chunk;
    }

    // MARK: - Formação acadêmica/titulação

    /// <summary>
    /// Cada diploma começa por um nível ("Doutorado/Mestrado/Graduação/…"). A coluna
    /// de períodos costuma vir achatada no topo, então dividimos pelo NÍVEL (marcador
    /// confiável) e tiramos o ano de "Ano de obtenção:" quando houver.
    /// </summary>
    private static readonly Regex FormacaoLevelRegex = new(
        @"^(P[óo]s[- ][Dd]outorado|Doutorado|Mestrado Profissional|Mestrado|Gradua[çc][ãa]o|Especializa[çc][ãa]o|Aperfei[çc]oamento|Curso [Tt]écnico|Ensino Fundamental|Ensino Médio|Livre-doc[êe]ncia|Resid[êe]ncia|Habilita[çc][ãa]o)\b");

    private static readonly Regex PeriodPrefixRegex = new(@"^\s*(\d{4}\s*-\s*(?:\d{4}|[Aa]tual)\s*)+");

    private static List<ParsedEntry> ParseFormacao(string body)
    {
        var lines = body.Split('\n').Select(l => l.Trim()).Where(l => !IsNoise(l) && l.Length > 0).ToList();
        if (lines.Count == 0) return new List<ParsedEntry>();

        string StripPeriods(string l) => PeriodPrefixRegex.Replace(l, "").Trim();
        bool IsLevel(string l) => FormacaoLevelRegex.IsMatch(StripPeriods(l));

        // Sem marcadores reconhecíveis → cai no parser genérico.
        if (!lines.Any(IsLevel)) return ParseEntries(body, "Formação", banca: false);

        var chunks = new List<List<string>>();
        var buf = new List<string>();
        foreach (var l in lines)
        {
            if (IsLevel(l) && buf.Count > 0) { chunks.Add(buf); buf = new List<string>(); }
            buf.Add(l);
        }
        if (buf.Count > 0) chunks.Add(buf);

        var entries = new List<ParsedEntry>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            if (!IsLevel(c[0])) continue;
            var text = string.Join(" ", c);
            var title = StripPeriods(c[0]).Trim(' ', '.');
            // instituição = linha "Universidade…/Instituto…/Faculdade…" do bloco
            var instLine = c.FirstOrDefault(l =>
            {
                var n = NormalizeHeader(l);
                return n.StartsWith("universidade") || n.StartsWith("instituto")
                    || n.StartsWith("faculdade") || n.StartsWith("fundacao") || n.StartsWith("centro");
            });
            var inst = instLine is null ? "" : instLine.Split(',')[0].Trim();
            var yStr = FirstMatch(text, @"Ano de obten[çc][ãa]o:\s*(\d{4})");
            var y = int.TryParse(yStr, out var yy) ? yy : ExtractYear(text);
            // Diplomas duplos no mesmo período às vezes perdem a própria coluna de
            // período por um artefato de quebra de página — herda o ano do diploma
            // imediatamente anterior em vez de ficar sem ano.
            if (y == 0 && entries.Count > 0) y = entries[^1].Year;
            entries.Add(new ParsedEntry(
                rawText: text, title: inst.Length == 0 ? title : $"{title} — {inst}",
                kind: "Formação", year: y, authors: "", venue: inst,
                doi: "", isbn: "", order: i));
        }
        return entries;
    }

    // MARK: - Prêmios e títulos

    /// <summary>
    /// Os prêmios vêm com a coluna de anos achatada numa única linha
    /// ("2023 2017 2012 &lt;prêmio1&gt; …") e cada prêmio termina no nome da
    /// instituição concedente (última palavra Capitalizada / acrônimo).
    /// </summary>
    private static List<ParsedEntry> ParsePremios(string body)
    {
        var lines = body.Split('\n').Select(l => l.Trim()).Where(l => !IsNoise(l) && l.Length > 0).ToList();
        if (lines.Count == 0) return new List<ParsedEntry>();

        // Remove conteúdo que vaza de "Áreas de atuação"/"Idiomas" por achatamento de colunas.
        bool IsLeak(string l)
        {
            var n = NormalizeHeader(l);
            if (n.Contains("grande area") || n.Contains("subarea") || n.StartsWith("area:")
                || n.StartsWith("/ area")) return true;
            if (n.StartsWith("compreende") || n.StartsWith("fala ") || n.StartsWith("le ")
                || n.StartsWith("escreve")) return true;
            if (n.StartsWith("periodico") || n.StartsWith("ordenar por") || n.StartsWith("ordem ")) return true;
            return false;
        }
        lines = lines.Where(l => !IsLeak(l)).ToList();
        if (lines.Count == 0) return new List<ParsedEntry>();

        // Coluna de anos: na 1ª linha ("2023 2017 2012 …") OU linhas isoladas só com o ano.
        var years = new List<int>();
        var rest = new List<string>();
        var headMatch = Regex.Match(lines[0], @"^((?:19|20)\d{2}[\s,]+)+");
        if (headMatch.Success)
        {
            years = Regex.Matches(headMatch.Value, @"\d+").Select(m => int.Parse(m.Value)).ToList();
            var head = lines[0][headMatch.Length..].Trim();
            rest = (head.Length == 0 ? new List<string>() : new List<string> { head })
                .Concat(lines.Skip(1)).ToList();
        }
        else
        {
            foreach (var l in lines)
            {
                if (Regex.IsMatch(l, @"^(19|20)\d{2}$") && int.TryParse(l, out var y)) years.Add(y);
                else rest.Add(l);
            }
        }

        // Delimitador entre prêmios. Dois layouts: prêmios de 1 linha sem ponto final
        // (terminam no nome da instituição) → quebra numa palavra Capitalizada;
        // prêmios multi-linha que terminam em ponto → quebra no ponto final.
        bool EndsAtInstitution(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            var w = parts[^1].Trim('.', ',', ';', ')');
            return w.Length > 0 && char.IsUpper(w[0]);
        }
        var usePeriod = rest.Any(l => l.EndsWith('.'));
        Func<string, bool> isBoundary = usePeriod ? (l => l.EndsWith('.')) : EndsAtInstitution;

        var awards = new List<string>();
        var buf = new List<string>();
        foreach (var line in rest)
        {
            buf.Add(line);
            if (isBoundary(line)) { awards.Add(string.Join(" ", buf)); buf = new List<string>(); }
        }
        if (buf.Count > 0) awards.Add(string.Join(" ", buf));

        var entries = new List<ParsedEntry>();
        for (var idx = 0; idx < awards.Count; idx++)
        {
            var a = awards[idx];
            if (a.Length < 6) continue;
            var inlineYear = ExtractYear(a);
            var y = idx < years.Count ? years[idx] : (inlineYear > 0 ? inlineYear : (years.Count > 0 ? years[^1] : 0));
            entries.Add(new ParsedEntry(
                rawText: a, title: a, kind: "Prêmio/Título",
                year: y, authors: "", venue: "", doi: "", isbn: "", order: idx));
        }
        return entries;
    }

    // MARK: - Entradas genéricas (artigos, orientações, eventos, bancas…)

    private static readonly Regex BareNumRegex = new(@"^\s*\d{1,3}\.\s*$");
    private static readonly Regex NumberStartRegex = new(@"^\s*\d{1,3}\.(\s+\S|\s*$)");
    private static readonly Regex ClusterRegex = new(@"^\s*(\d{1,3}\.\s+){2,}");
    private static readonly Regex PeriodRegex = new(@"\b\d{4}\s*-\s*(?:\d{4}|[Aa]tual)\b");
    private static readonly Regex BancaMarkRegex = new(@"Participaç[ãa]o\s+em\s+[Bb]anca\s+de", RegexOptions.IgnoreCase);
    private static readonly Regex CitacoesSuffixRegex = new(@"\s*Citações:\s*\d+(\s*\|\s*\d+)?\s*$");
    private static readonly Regex EndsAtYearRegex = new(@"(19|20)\d{2}\.?$");

    /// <summary>Tipos onde toda entrada real começa com "SOBRENOME, Iniciais.." — usado para filtrar fragmentos sem rótulo que vazariam como prefixo da entrada seguinte.</summary>
    private static readonly HashSet<string> CitationKinds = new()
    {
        "Artigo", "Livro/Capítulo", "Trabalho em evento", "Produção técnica",
        "Mídia", "Corpo editorial",
    };

    private static List<ParsedEntry> ParseEntries(string body, string kind, bool banca)
    {
        var lines = body.Split('\n').Select(l => l.Trim())
            .Where(l => !IsNoise(l) && !IsQualisAnnotation(l)).ToList();

        if (lines.Count == 0) return new List<ParsedEntry>();

        // Numeração DESTACADA: quando a coluna de números vem empilhada ("1.\n2.\n3.")
        // separada dos corpos, os marcadores ficam órfãos. Detecta >=2 marcadores "N."
        // isolados SEQUENCIAIS e os remove — aí a divisão recai na âncora de ano.
        bool IsBareNum(string l) => BareNumRegex.IsMatch(l);

        var runLen = 0; var maxRun = 0; var prevVal = int.MinValue;
        foreach (var l in lines.Where(l => l.Length > 0))
        {
            if (IsBareNum(l) && int.TryParse(l.Trim(' ', '.'), out var v))
            {
                runLen = v == prevVal + 1 ? runLen + 1 : 1;
                prevVal = v;
                maxRun = Math.Max(maxRun, runLen);
            }
            else { runLen = 0; prevVal = int.MinValue; }
        }
        var detachedNumbering = maxRun >= 2;
        if (detachedNumbering) lines = lines.Where(l => !IsBareNum(l)).ToList();

        // "N. conteúdo" ou "N." sozinho na linha (numeração quebrada em duas linhas).
        bool IsNumberStart(string l) => NumberStartRegex.IsMatch(l);
        string StripCluster(string l)
        {
            var m = ClusterRegex.Match(l);
            return m.Success ? l[(m.Index + m.Length)..] : l;
        }

        var hasCluster = lines.Any(l => ClusterRegex.IsMatch(l));
        var numberedCount = lines.Count(IsNumberStart);

        // Intervalo de anos "AAAA - AAAA" / "AAAA - Atual" (formação, projetos, editorial)
        var joinedAll = string.Join(" ", lines);
        var periodMatches = PeriodRegex.Matches(joinedAll);

        var chunks = new List<string>();
        var strippedPeriod = false;

        // Bancas: o marcador "Participação em banca de" é o delimitador mais confiável.
        var bancaJoined = string.Join(" ", lines);
        var bancaMarks = banca ? BancaMarkRegex.Matches(bancaJoined).Cast<Match>().ToList() : new List<Match>();

        if (banca && detachedNumbering && bancaMarks.Count >= 2)
        {
            // Numeração destacada → usa o marcador de banca (a numeração não é confiável).
            for (var i = 0; i < bancaMarks.Count; i++)
            {
                var start = bancaMarks[i].Index;
                var end = i + 1 < bancaMarks.Count ? bancaMarks[i + 1].Index : bancaJoined.Length;
                var chunk = bancaJoined[start..end];
                // remove números soltos remanescentes ("3. 4. 5.")
                chunk = Regex.Replace(chunk, @"\s(\d{1,3}\.\s){1,}", " ");
                chunks.Add(chunk);
            }
        }
        else if (!hasCluster && numberedCount >= 2)
        {
            // Divisão por numeração de linha "N. "
            var buf = new List<string>();
            foreach (var line in lines.Where(l => l.Length > 0))
            {
                if (IsNumberStart(line) && buf.Count > 0) { chunks.Add(string.Join(" ", buf)); buf = new List<string>(); }
                buf.Add(StripLeadingNumber(line));
            }
            if (buf.Count > 0) chunks.Add(string.Join(" ", buf));
        }
        else if (!hasCluster && periodMatches.Count >= 2)
        {
            // Divisão no início de cada intervalo de anos (colunas período/conteúdo achatadas)
            strippedPeriod = true;
            for (var i = 0; i < periodMatches.Count; i++)
            {
                var start = periodMatches[i].Index;
                var end = i + 1 < periodMatches.Count ? periodMatches[i + 1].Index : joinedAll.Length;
                if (end > start) chunks.Add(joinedAll[start..end]);
            }
        }
        else
        {
            // Divisão por âncora de ano (colunas achatadas / sem numeração).
            var cleaned = lines.Select(l => StripLeadingNumber(StripCluster(l)))
                .Where(l => l.Trim().Length > 0).ToList();

            bool LooksLikeStrayFragment(string l)
            {
                if (!CitationKinds.Contains(kind) || l.Length > 160) return false;
                var prefix = l.Length > 50 ? l[..50] : l;
                return !prefix.Contains(',') && !HasYear(l);
            }

            var buf = new List<string>();
            for (var li = 0; li < cleaned.Count; li++)
            {
                var line = cleaned[li];
                // Metadados e continuações após o fim de uma entrada pertencem a ela —
                // anexa ao último chunk em vez de iniciar um novo.
                if (buf.Count == 0 && chunks.Count > 0
                    && (IsMetadataLine(line) || IsContinuationStart(line) || LooksLikeStrayFragment(line)))
                {
                    chunks[^1] += " " + line;
                    continue;
                }
                buf.Add(line);
                var joined = string.Join(" ", buf);
                var nextOpensParen = li + 1 < cleaned.Count && cleaned[li + 1].Trim().StartsWith("(");
                // Ignora a anotação "Citações: N | N" no fim da linha ao testar o fechamento.
                var testLine = CitacoesSuffixRegex.Replace(line, "");
                // A linha termina no ano de realização?
                var endsAtYear = EndsAtYearRegex.IsMatch(testLine.Trim());
                // Não quebra se o ano fecha a linha e o tipo/título "(…)" vem na próxima
                var suppress = endsAtYear && nextOpensParen;
                if (HasYear(joined) && (EndsSentence(testLine) || EndsWithYear(testLine)) && !suppress)
                {
                    chunks.Add(joined); buf = new List<string>();
                }
            }
            if (buf.Count > 0) chunks.Add(string.Join(" ", buf));
        }

        // Rede de segurança (todos os modos): chunk que COMEÇA com metadados pertence
        // ao chunk anterior — funde.
        var mergedChunks = new List<string>();
        foreach (var c in chunks)
        {
            var t = c.Trim();
            if (IsMetadataLine(t) && mergedChunks.Count > 0) mergedChunks[^1] += " " + t;
            else mergedChunks.Add(c);
        }
        chunks = mergedChunks;

        // Converte chunks em entradas
        var entries = new List<ParsedEntry>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var text = chunks[i].Trim();
            var periodYear = strippedPeriod ? ExtractYear(text.Length > 14 ? text[..14] : text) : 0;
            if (strippedPeriod)
            {
                // Remove o prefixo "AAAA - AAAA " para o título ser o nome do curso/projeto
                text = Regex.Replace(text, @"^\s*\d{4}\s*-\s*(?:\d{4}|[Aa]tual)\s*", "");
            }
            if (text.Length < 8) continue;
            // Fragmento curto sem ano (continuação quebrada pela paginação) — não é entrada real.
            if (!strippedPeriod && text.Length < 50 && ExtractYear(text) == 0) continue;
            if (banca)
            {
                entries.Add(MakeBancaEntry(text, i));
            }
            else
            {
                var e = MakeEntry(text, kind, i);
                if (periodYear > 0) e.Year = periodYear;
                entries.Add(e);
            }
        }
        return entries;
    }

    private static ParsedEntry MakeEntry(string text, string kind, int order)
    {
        var (title, authors, venue) = ExtractTitleAuthorsVenue(text);
        return new ParsedEntry(
            rawText: text, title: title, kind: kind, year: ExtractYear(text),
            authors: authors, venue: venue,
            doi: ExtractDoi(text), isbn: ExtractIsbn(text), order: order,
            portaria: string.Join(" ", SimilarityMatcher.PortariaPairs(text)),
            issn: string.Join(" ", SimilarityMatcher.IssnNumbers(text)),
            edital: string.Join(" ", SimilarityMatcher.EditalNumbers(text)));
    }

    private static ParsedEntry MakeBancaEntry(string text, int order)
    {
        // "… Participação em banca de <Candidato>. <Título>, <ano>. (<Área>) <Instituição>."
        var candidate = FirstMatch(text, @"banca de\s+([^.]+)\.").Trim();
        var institution = FirstMatch(text, @"\)\s*([^.()]+)\.?\s*$").Trim();
        var titleCore = candidate.Length == 0 ? text : candidate;
        return new ParsedEntry(
            rawText: text, title: titleCore, kind: "Banca",
            year: ExtractYear(text), authors: "", venue: institution,
            doi: "", isbn: "", order: order,
            portaria: string.Join(" ", SimilarityMatcher.PortariaPairs(text)),
            issn: "",
            edital: string.Join(" ", SimilarityMatcher.EditalNumbers(text)));
    }

    // MARK: - Bancas por nível

    /// <summary>
    /// O Lattes agrupa "Participação em banca de trabalhos de conclusão" por nível,
    /// cada um com sua própria linha-marcador solta no corpo. Divide o corpo por
    /// esses marcadores para identificar de qual se trata; sem marcadores, mantém
    /// tudo num único grupo (nível vazio).
    /// </summary>
    private static readonly (string Match, string Display)[] BancaLevelMarkers =
    {
        ("mestrado", "Mestrado"),
        ("doutorado", "Doutorado"),
        ("exame de qualificacao de mestrado", "Qualificação de Mestrado"),
        ("exame de qualificacao de doutorado", "Qualificação de Doutorado"),
        ("graduacao", "Graduação"),
    };

    private static List<(string Nivel, List<ParsedEntry> Entries)> ParseBancaPorNivel(string body)
    {
        var lines = body.Split('\n').Select(l => l.Trim()).ToList();
        var numClusterRegex = new Regex(@"^(\d{1,3}\.\s*)+");

        // Retorna o nível da linha e, se veio com uma coluna de numeração destacada
        // colada ("1. 2. 3. 4. 5. 6. Doutorado"), quantos números tinha ("6").
        (string Display, int NumCount)? LevelFor(string l)
        {
            var n = NormalizeHeader(l);
            var m = numClusterRegex.Match(n);
            if (!m.Success)
            {
                foreach (var (match, display) in BancaLevelMarkers)
                    if (match == n) return (display, 0);
                return null;
            }
            var rest = n[(m.Index + m.Length)..];
            var count = m.Value.Count(ch => ch == '.');
            foreach (var (match, display) in BancaLevelMarkers)
                if (match == rest) return (display, count);
            return null;
        }

        if (!lines.Any(l => LevelFor(l) is not null))
            return new List<(string, List<ParsedEntry>)> { ("", ParseEntries(body, "Banca", banca: true)) };

        // Linhas onde cada entrada real começa. A busca é no texto UNIDO por espaço —
        // a frase às vezes quebra entre duas linhas e escaparia de uma checagem
        // linha a linha, fazendo a contagem abaixo perder entradas.
        var offsets = new List<int>();
        var acc = 0;
        foreach (var l in lines) { offsets.Add(acc); acc += l.Length + 1; }
        var joined = string.Join(" ", lines);
        var matchLocations = BancaMarkRegex.Matches(joined).Select(m => m.Index).ToList();
        var entryStartLines = new HashSet<int>();
        foreach (var loc in matchLocations)
        {
            var idx = 0;
            for (var k = 0; k < offsets.Count; k++) if (offsets[k] <= loc) idx = k;
            entryStartLines.Add(idx);
        }

        var groups = new List<(string Nivel, List<string> Lines)>();
        var current = "";
        var buf = new List<string>();
        // O extrator às vezes reordena o texto e cola a coluna de numeração destacada
        // do nível ATUAL no marcador do PRÓXIMO nível — mas essas entradas que vêm a
        // seguir ainda são do nível anterior. Adia a troca até que comece a
        // (N+1)-ésima entrada real — não na N-ésima, para não cortar no meio do resto
        // do texto da última entrada ainda pertencente ao nível anterior.
        string? pendingLevel = null;
        var pendingCount = 0;
        var pendingSeen = 0;

        for (var li = 0; li < lines.Count; li++)
        {
            var l = lines[li];
            var lvl = LevelFor(l);
            if (lvl is not null)
            {
                if (buf.Count == 0 && lvl.Value.NumCount > 0 && current.Length > 0 && pendingLevel is null)
                {
                    pendingLevel = lvl.Value.Display; pendingCount = lvl.Value.NumCount; pendingSeen = 0;
                    continue;
                }
                groups.Add((current, buf));
                current = lvl.Value.Display; buf = new List<string>();
                pendingLevel = null;
                continue;
            }
            if (pendingLevel is not null && entryStartLines.Contains(li))
            {
                pendingSeen++;
                if (pendingSeen > pendingCount)
                {
                    groups.Add((current, buf));
                    current = pendingLevel; buf = new List<string> { l };
                    pendingLevel = null;
                    continue;
                }
            }
            buf.Add(l);
        }
        groups.Add((current, buf));

        var result = new List<(string, List<ParsedEntry>)>();
        foreach (var g in groups)
        {
            if (g.Nivel.Length == 0) continue;
            var entries = ParseEntries(string.Join("\n", g.Lines), "Banca", banca: true);
            if (entries.Count > 0) result.Add((g.Nivel, entries));
        }
        return result;
    }

    // MARK: - Helpers de texto

    private static string NormalizeHeader(string s) => TextNormalization.FoldDiacriticsLower(s).Trim();

    private static bool IsNoise(string line)
    {
        if (line.Contains("wwws.cnpq.br")) return true;
        if (line.StartsWith("impcv.trata")) return true;
        if (Regex.IsMatch(line, @"^\d{2}/\d{2}/\d{4},?\s*\d{1,2}:\d{2}")) return true;
        if (line == "Currículo Lattes") return true;
        if (line is "Ordenar por" or "Ordem Cronológica" or "Ordem de Importância") return true;
        return false;
    }

    /// <summary>Linha de metadados que alguns exports do Lattes inserem sob cada entrada. Pertence à entrada ANTERIOR — nunca deve iniciar uma nova.</summary>
    private static bool IsMetadataLine(string line)
    {
        var n = NormalizeHeader(line);
        return n.StartsWith("palavras-chave") || n.StartsWith("referencias adicionais")
            || n.StartsWith("home page") || n.StartsWith("meio de divulgacao");
    }

    /// <summary>Linha que claramente CONTINUA a entrada anterior (nunca inicia uma nova). Entradas reais do Lattes começam com Maiúscula, número ou "(".</summary>
    private static bool IsContinuationStart(string line)
    {
        if (line.Length == 0) return false;
        var f = line[0];
        if (line.StartsWith("http") || line.StartsWith("www.")) return true;
        // "Home page:" quebrado em duas linhas deixa a URL sozinha entre colchetes.
        if (line.StartsWith("[http") || line.StartsWith("[www.")) return true;
        if (char.IsLower(f)) return true;
        if (f == '-' || f == '–') return true;
        var n = NormalizeHeader(line);
        if (n.StartsWith("portaria") || n.StartsWith("edital")) return true;
        if (line.Length <= 90 && Regex.IsMatch(n, @"(portaria|edital)\s*n")) return true;
        return false;
    }

    /// <summary>Linha de anotação Qualis que o Lattes insere sob artigos quando o autor anotou o estrato. Descartada para não poluir título/ano do artigo (o Qualis é recalculado pelo app).</summary>
    private static bool IsQualisAnnotation(string line)
    {
        if (line.ToLowerInvariant().Contains("fonte qualis")) return true;
        return Regex.IsMatch(line, @"^(A[1-4]|B[1-5]|C|N[ãa]o classificado)\s*,\s*ISSN", RegexOptions.IgnoreCase);
    }

    private static bool HasYear(string s) => Regex.IsMatch(s, @"\b(19|20)\d{2}\b");

    private static bool EndsSentence(string line)
    {
        var t = line.Trim();
        return t.EndsWith('.') || t.EndsWith(".\"") || t.EndsWith("\".") || t.EndsWith(").");
    }

    /// <summary>Linha que termina com um ano (terminador de entrada "…, 2026" / "…2013.").</summary>
    private static bool EndsWithYear(string line) => Regex.IsMatch(line.Trim(), @"(19|20)\d{2}\.?$");

    private static readonly Regex StripLeadingNumberRegex = new(@"^\s*\d{1,3}\.\s*");

    private static string StripLeadingNumber(string l) => StripLeadingNumberRegex.Replace(l, "");

    private static string FirstMatch(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success && m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : "";
    }

    public static int ExtractYear(string text)
    {
        var matches = Regex.Matches(text, @"\b(19|20)\d{2}\b");
        if (matches.Count == 0) return 0;
        return int.TryParse(matches[^1].Value, out var y) ? y : 0;
    }

    private static readonly Regex AuthorLineRegex = new(@"^[A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ][A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ\s,\.;\-']{4,}$");
    private static readonly Regex CitacoesParenRegex = new(@"\s*\(Citações:.*?\)");

    private static (string Title, string Authors, string Venue) ExtractTitleAuthorsVenue(string text)
    {
        var sentences = SplitSentences(text);
        if (sentences.Count == 0) return (text, "", "");

        var authors = "";
        var title = "";
        var venue = "";

        if (sentences.Count == 1)
        {
            title = sentences[0];
        }
        else
        {
            var first = sentences[0];
            if (AuthorLineRegex.IsMatch(first) || (first.Contains(';') && first == first.ToUpperInvariant()))
            {
                authors = first;
                title = sentences.Count > 1 ? sentences[1] : "";
                venue = sentences.Count > 2 ? sentences[2] : "";
            }
            else
            {
                title = first;
                venue = sentences.Count > 1 ? sentences[1] : "";
            }
        }

        title = CitacoesParenRegex.Replace(title, "").Trim();
        return (title.Length == 0 ? sentences[0] : title, authors, venue);
    }

    private static readonly HashSet<string> Abbreviations = new()
    {
        "v", "n", "p", "ed", "org", "vol", "pp", "op", "cit", "et", "al", "dr", "ph",
    };

    private static List<string> SplitSentences(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var chars = text.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            current.Append(chars[i]);
            if (chars[i] == '.' && i + 1 < chars.Length && chars[i + 1] == ' ')
            {
                var currentStr = current.ToString();
                var lastWord = currentStr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault() ?? "";
                var word = lastWord.ToLowerInvariant().Replace(".", "");
                var isAbbrev = Abbreviations.Contains(word) || word.Length == 1;
                if (!isAbbrev)
                {
                    parts.Add(currentStr.Trim());
                    current.Clear();
                    i += 2;
                    continue;
                }
            }
            i++;
        }
        var tail = current.ToString().Trim();
        if (tail.Length > 0) parts.Add(tail);
        return parts.Where(p => p.Length > 0).ToList();
    }

    private static string ExtractDoi(string text) => FirstMatch(text, @"(10\.\d{4,}/[^\s,;]+)");

    private static string ExtractIsbn(string text) => FirstMatch(text, @"ISBN[:\s]*([\dXx\-]{10,17})");
}
