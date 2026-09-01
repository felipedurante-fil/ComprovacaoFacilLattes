using System.Text.RegularExpressions;
using ComprovacaoFacilLattes.Core.Text;

namespace ComprovacaoFacilLattes.Core.Matching;

/// <summary>
/// Scoring e ranking de comprovante↔entrada — parte 100% portável de
/// <c>CertificateIndexer.swift</c> (a extração de texto/OCR, não-portável, fica na
/// camada Infrastructure).
/// </summary>
public static class CertificateMatcher
{
    public sealed class EntryFields
    {
        public string Title = "";
        public string Authors = "";
        public string Venue = "";
        public string Kind = "";
        public string Portaria = "";
        public string Edital = "";
        public string Issn = "";
        public string Doi = "";
        public int Year;
        public int EndYear;
        public string HashKey = "";
    }

    public readonly record struct ScoredMatch(int Index, double Score);

    /// <summary>Bônus aplicado quando o tipo da entrada combina com a pasta do certificado.</summary>
    private const double FolderBonus = 0.20;

    private static HashSet<string> Tokens(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    private static readonly Regex YearRegex = new(@"\b(19|20)\d{2}\b");

    /// <summary>Extrai anos plausíveis (1990–2035) de um texto.</summary>
    public static HashSet<int> YearsIn(string text)
    {
        var years = new HashSet<int>();
        foreach (Match m in YearRegex.Matches(text))
        {
            if (int.TryParse(m.Value, out var y) && y is >= 1990 and <= 2035) years.Add(y);
        }
        return years;
    }

    /// <summary>Ajusta o score conforme a proximidade entre o ano do certificado e o da entrada.</summary>
    private static double ApplyYear(double score, HashSet<int> certYears, int entryYear, int entryEndYear = 0)
    {
        if (score <= 0 || entryYear <= 0 || certYears.Count == 0) return score;
        // Período com faixa (vínculo/atividade): "Atual"/aberto vai até o ano corrente.
        if (entryEndYear >= entryYear || entryEndYear == 0)
        {
            var endY = entryEndYear == 0 ? DateTime.Now.Year : entryEndYear;
            if (entryEndYear != 0 || endY > entryYear) // só trata como faixa se houver intervalo real
            {
                if (certYears.Any(y => y >= entryYear && y <= endY)) return Math.Min(1.0, score + 0.06);
                var gap0 = certYears.Min(y => Math.Min(Math.Abs(y - entryYear), Math.Abs(y - endY)));
                if (gap0 >= 3) return score * 0.80;
                return score;
            }
        }
        // Ano único
        var gap = certYears.Min(y => Math.Abs(y - entryYear));
        if (gap == 0) return Math.Min(1.0, score + 0.06); // mesmo ano → reforço
        if (gap >= 3) return score * 0.80;                // distante → penaliza
        return score;                                     // ±1/±2 → neutro
    }

    /// <summary>Retorna as melhores correspondências (desc) para um certificado.</summary>
    public static List<ScoredMatch> RankedMatches(
        string text, string certKey, HashSet<int> certYears,
        IReadOnlyList<EntryFields> entryFields, HashSet<string> folderKinds,
        IReadOnlyDictionary<string, double> idf, HashSet<string> rejected)
    {
        if (text.Length == 0) return new List<ScoredMatch>();
        var capped = text.Length > 4000 ? text[..4000] : text;

        var certPort = SimilarityMatcher.PortariaPairs(capped);
        var certEdital = SimilarityMatcher.EditalNumbers(capped);
        var certDoi = SimilarityMatcher.DoiNumbers(capped);
        var certIssn = SimilarityMatcher.IssnNumbers(capped);
        var certIsPortaria = certPort.Count > 0 || capped.ToLowerInvariant().Contains("portaria");
        var certHasPubId = SimilarityMatcher.HasPublicationIdentifier(capped);

        var outList = new List<ScoredMatch>();
        for (var idx = 0; idx < entryFields.Count; idx++)
        {
            var f = entryFields[idx];
            // Gates
            if ((f.Kind == "Artigo" || f.Kind == "Livro/Capítulo") && !certHasPubId) continue;
            if (certIsPortaria && (f.Kind == "Orientação" || f.Kind == "Formação")) continue;
            if (rejected.Contains($"{certKey}||{f.HashKey}")) continue;

            double score;
            var isIdentifier = false;

            // Identificadores precisos (quase-certeza) — têm prioridade sobre texto.
            var portScore = certPort.Count == 0 || f.Portaria.Length == 0
                ? 0 : SimilarityMatcher.PortariaMatchScore(certPort, Tokens(f.Portaria));
            if (portScore > 0)
            {
                score = portScore; isIdentifier = true;
            }
            else if (certEdital.Count > 0 && f.Edital.Length > 0 && certEdital.Overlaps(Tokens(f.Edital)))
            {
                score = 0.99; isIdentifier = true;
            }
            else if (certDoi.Count > 0 && f.Doi.Length > 0 && certDoi.Overlaps(Tokens(f.Doi)))
            {
                score = 1.0; isIdentifier = true;
            }
            else
            {
                score = SimilarityMatcher.Score(capped, f.Title, f.Authors, f.Venue, idf);
                // ISSN identifica o periódico (não o artigo) → reforça quando o título também casa
                if (score > 0.2 && certIssn.Count > 0 && f.Issn.Length > 0 && certIssn.Overlaps(Tokens(f.Issn)))
                {
                    score = Math.Min(1.0, score + 0.15);
                }
            }

            // Ano (desambiguação) — usa a faixa do período quando há (vínculo/atividade)
            score = ApplyYear(score, certYears, f.Year, f.EndYear);

            if (!isIdentifier)
            {
                // Pasta indica a categoria provável; texto fica logo abaixo dos identificadores
                if (folderKinds.Count > 0 && folderKinds.Contains(f.Kind) && score > 0)
                    score += FolderBonus;
                score = Math.Min(0.97, score);
            }

            if (score > 0) outList.Add(new ScoredMatch(idx, Math.Min(1.0, score)));
        }
        return outList.OrderByDescending(m => m.Score).ToList();
    }

    /// <summary>Item bruto usado por <see cref="GlobalAssign"/>.</summary>
    public readonly record struct RankedItem(List<ScoredMatch> Ranked);

    /// <summary>
    /// Atribuição global: quando o top-1 e o top-2 de um certificado estão muito
    /// próximos e a entrada do top-1 já está bem coberta, prefere a entrada ainda
    /// descoberta — evita acúmulo de certificados numa mesma entrada genérica.
    /// </summary>
    public static List<int> GlobalAssign(IReadOnlyList<RankedItem> items, double guessFloor)
    {
        var coverage = new Dictionary<int, int>();
        foreach (var r in items)
        {
            if (r.Ranked.Count > 0 && r.Ranked[0].Score >= guessFloor)
                coverage[r.Ranked[0].Index] = coverage.GetValueOrDefault(r.Ranked[0].Index) + 1;
        }

        var chosen = new List<int>();
        foreach (var r in items)
        {
            if (r.Ranked.Count == 0 || r.Ranked[0].Score < guessFloor) { chosen.Add(-1); continue; }
            var top = r.Ranked[0];
            var pick = 0;
            if (r.Ranked.Count >= 2)
            {
                var b = r.Ranked[1];
                if (top.Score - b.Score <= 0.08
                    && coverage.GetValueOrDefault(top.Index) >= 2
                    && coverage.GetValueOrDefault(b.Index) == 0
                    && b.Score >= guessFloor)
                {
                    pick = 1;
                    coverage[top.Index] = coverage.GetValueOrDefault(top.Index) - 1;
                    coverage[b.Index] = coverage.GetValueOrDefault(b.Index) + 1;
                }
            }
            chosen.Add(pick);
        }
        return chosen;
    }

    /// <summary>Mapeia o nome da pasta (e subpastas) para os tipos de entrada prováveis. Ex.: pasta "Participação em Eventos" → tipos de evento.</summary>
    public static HashSet<string> InferFolderKinds(string path)
    {
        var n = TextNormalization.FoldDiacriticsLower(path);
        var k = new HashSet<string>();
        bool Has(string s) => n.Contains(s);

        if (Has("banca")) k.Add("Banca");
        if (Has("aprovacao")) k.Add("Vínculo institucional");
        if (Has("evento") || Has("apresenta") || Has("poster") || Has("debatedor")
            || Has("mediador") || Has("mesa") || Has("palestra") || Has("conferen")
            || Has("congress") || Has("coloquio") || Has("simposio") || Has("semana"))
        {
            k.UnionWith(new[] { "Evento", "Apresentação", "Organização de evento", "Trabalho em evento" });
        }
        if (Has("organizacao")) k.Add("Organização de evento");
        if (Has("orienta") || Has("monitoria")) k.Add("Orientação");
        if (Has("parecer") || Has("tecnic")) k.Add("Produção técnica");
        if (Has("formacao") || Has("curso") || Has("alura") || Has("lingua")
            || Has("idioma") || Has("capacita")) k.Add("Formação");
        if (Has("projeto") || Has("extensao") || Has("pesquisa")) k.Add("Projeto");
        if (Has("premio") || Has("titulo")) k.Add("Prêmio/Título");
        if (Has("edito")) k.UnionWith(new[] { "Corpo editorial", "Mídia" });
        if (Has("didatica") || Has("disciplina") || Has("experiencia") || Has("docencia"))
            k.UnionWith(new[] { "Disciplina ministrada", "Vínculo institucional" });
        if (Has("bolsa")) k.UnionWith(new[] { "Formação", "Projeto" });
        return k;
    }
}
