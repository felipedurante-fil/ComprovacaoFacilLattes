using System.Text.RegularExpressions;
using ComprovacaoFacilLattes.Core.Text;

namespace ComprovacaoFacilLattes.Core.Matching;

/// <summary>
/// Mede o quanto um certificado comprova uma entrada do Lattes.
///
/// Lições aprendidas (currículo do próprio docente):
///  • O nome do autor NÃO é discriminante — aparece em quase todas as entradas e em
///    quase todos os certificados. Por isso é ignorado no score.
///  • O sinal forte é o TÍTULO aparecendo no certificado, com destaque para trechos
///    contíguos (frase) do título. Para eventos, o nome do evento/local é o sinal
///    alternativo (o título do trabalho pode não constar no certificado).
/// </summary>
public static class SimilarityMatcher
{
    private static readonly HashSet<string> Stopwords = new()
    {
        "para", "por", "com", "dos", "das", "uma", "que", "nas", "nos",
        "ao", "aos", "pela", "pelo", "the", "and", "of", "in", "on", "de",
        "do", "da", "em", "no", "na", "os", "as", "um", "se", "sua", "seu",
        "como", "sobre", "entre", "ou", "etc", "este", "esta", "pelos", "pelas",
        "anais", "revista", "journal", "vol",
        // Termos administrativos — não identificam um comprovante específico
        "carga", "horaria", "hora", "horas", "outras", "informacoes",
        "atual", "nivel", "regime", "certificado", "certificamos",
        "declaracao", "declaramos", "total",
    };

    // MARK: - IDF (raridade de palavras)

    /// <summary>
    /// Constrói pesos IDF a partir dos títulos das entradas: palavras raras valem mais,
    /// palavras comuns ("filosofia", "universidade", "silva") valem quase nada.
    /// </summary>
    public static Dictionary<string, double> BuildIdf(IEnumerable<string> titles)
    {
        var titleList = titles as IList<string> ?? titles.ToList();
        var n = Math.Max(titleList.Count, 1);
        var df = new Dictionary<string, int>();
        foreach (var title in titleList)
        {
            var toks = Normalize(title).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(IsMeaningful).ToHashSet();
            foreach (var t in toks) df[t] = df.GetValueOrDefault(t) + 1;
        }

        var idf = new Dictionary<string, double>();
        foreach (var (t, c) in df) idf[t] = Math.Log((double)n / (1 + c)) + 0.5; // sempre >= ~0.5
        return idf;
    }

    private static double Weight(string token, IReadOnlyDictionary<string, double>? idf)
    {
        if (idf is null) return 1.0;
        if (idf.TryGetValue(token, out var w)) return w;
        return Math.Log(idf.Count == 0 ? 2 : idf.Count) + 0.5; // raro/desconhecido: peso alto
    }

    // MARK: - API

    /// <summary>Score 0.0–1.0 entre o texto do certificado e os campos da entrada. <paramref name="idf"/> (opcional) pondera as palavras pela raridade.</summary>
    public static double Score(string certificateText, string title, string authors = "", string venue = "",
        IReadOnlyDictionary<string, double>? idf = null)
    {
        var cert = Normalize(certificateText);
        if (cert.Length == 0) return 0;
        var certTokens = cert.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        // --- Sinal de TÍTULO (cobertura ponderada por IDF) ---
        var titleWords = Normalize(title).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var titleSig = titleWords.Where(IsMeaningful).ToList();
        var matched = titleSig.Where(certTokens.Contains).ToList();
        var totalWeight = titleSig.Sum(t => Weight(t, idf));
        var matchedWeight = matched.Sum(t => Weight(t, idf));
        var tokenCov = totalWeight > 0 ? matchedWeight / totalWeight : 0;

        var phrase = 0.0;
        if (matched.Count > 0) phrase = PhraseFraction(titleWords, cert);

        // Para títulos curtos (≤2 palavras) só vale a FRASE contígua — palavras soltas
        // genéricas ("Doutorado em Filosofia") não devem casar.
        var titleScore = phrase;
        if (titleSig.Count >= 3) titleScore = Math.Max(titleScore, tokenCov);
        if (matched.Count < 2 && phrase < 0.6) titleScore = 0;

        // --- Sinal de LOCAL/EVENTO ---
        var venueSig = Normalize(venue).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(IsMeaningful).ToList();
        var venueMatched = venueSig.Where(certTokens.Contains).ToList();
        var venueTotalW = venueSig.Sum(t => Weight(t, idf));
        var venueMatchedW = venueMatched.Sum(t => Weight(t, idf));
        var venueCov = venueTotalW > 0 ? venueMatchedW / venueTotalW : 0;

        var score = titleScore;
        if (titleScore < 0.4 && venueCov >= 0.6 && venueMatched.Count >= 2)
        {
            // Eventos: o certificado bate o nome do evento, não o título do trabalho
            score = venueCov * 0.8;
        }
        else if (venueCov >= 0.5 && titleScore >= 0.3)
        {
            // Reforço quando título e local aparecem juntos
            score = Math.Min(1.0, titleScore + 0.1);
        }

        return Math.Min(1.0, score);
    }

    /// <summary>Overload simples (texto único como "título").</summary>
    public static double Score(string certificateText, string entryText) =>
        Score(certificateText, entryText, "", "");

    // MARK: - Internos

    /// <summary>Indica se o texto contém um identificador de publicação (ISSN, ISBN ou DOI). Usado para impedir que documentos sem esses dados sejam vinculados a artigos/livros.</summary>
    public static bool HasPublicationIdentifier(string text)
    {
        var lower = text.ToLowerInvariant();
        // Exige a palavra-chave (ISSN/ISBN/DOI) ou um DOI explícito — evita confundir
        // intervalos de ano ("2019-2024") com ISSN.
        if (lower.Contains("issn") || lower.Contains("isbn") || lower.Contains("doi")) return true;
        return Regex.IsMatch(text, @"10\.\d{4,}/");
    }

    private static readonly Regex PortariaNumberRegex =
        new(@"portaria\s*(?:n[º°o.]?\s*)?(\d{2,6})", RegexOptions.IgnoreCase);

    /// <summary>Extrai números de portaria de um texto (ex.: "PORTARIA N 2090, DE…" → {"2090"}).</summary>
    public static HashSet<string> PortariaNumbers(string text) =>
        CaptureGroups(text, PortariaNumberRegex).ToHashSet();

    private static readonly Regex PortariaPairRegex = new(
        @"portaria\s*(?:n[º°o.]?\s*)?(\d{2,6})([\s\S]{0,50}?\b(?:19|20)\d{2}\b)?",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Extrai pares portaria nº+ano (ex.: "PORTARIA N 2891, DE 06 DE OUTUBRO DE 2022" →
    /// {"2891/2022"}). Quando não há ano próximo, devolve só o número ("2891"). O ano é
    /// buscado nos ~50 caracteres seguintes ao número.
    /// </summary>
    public static HashSet<string> PortariaPairs(string text)
    {
        var result = new HashSet<string>();
        foreach (Match m in PortariaPairRegex.Matches(text))
        {
            var num = m.Groups[1].Value;
            var year = "";
            if (m.Groups[2].Success)
            {
                var ym = Regex.Match(m.Groups[2].Value, @"(19|20)\d{2}");
                if (ym.Success) year = ym.Value;
            }
            result.Add(year.Length == 0 ? num : $"{num}/{year}");
        }
        return result;
    }

    /// <summary>
    /// Pontua a coincidência de portarias entre certificado e entrada: nº+ano iguais →
    /// 0.99; só o número (um dos lados sem ano) → 0.95; mesmo número mas anos
    /// diferentes → 0 (não é a mesma portaria).
    /// </summary>
    public static double PortariaMatchScore(IEnumerable<string> cert, IEnumerable<string> entry)
    {
        static (string number, string? year) Split(string s)
        {
            var idx = s.IndexOf('/');
            return idx < 0 ? (s, null) : (s[..idx], s[(idx + 1)..]);
        }

        var best = 0.0;
        foreach (var c in cert)
        {
            var (cn, cy) = Split(c);
            foreach (var e in entry)
            {
                var (en, ey) = Split(e);
                if (cn != en) continue;
                if (cy is not null && ey is not null)
                {
                    if (cy == ey) best = Math.Max(best, 0.99); // nº+ano exatos
                    // anos presentes e diferentes → ignora (portaria distinta)
                }
                else
                {
                    best = Math.Max(best, 0.95); // só o número
                }
            }
        }
        return best;
    }

    private static readonly Regex EditalRegex =
        new(@"edital\s*(?:n[º°o.]?\s*)?(\d{1,4}\s*/\s*\d{2,4})", RegexOptions.IgnoreCase);

    /// <summary>Extrai números de edital (ex.: "Edital nº 41/2024-PROGRAD" → {"41/2024"}).</summary>
    public static HashSet<string> EditalNumbers(string text) =>
        CaptureGroups(text, EditalRegex).Select(s => s.Replace(" ", "")).ToHashSet();

    private static readonly Regex IssnRegex =
        new(@"issn[:\s]*(\d{4}\s*-\s*\d{3}[\dxX])", RegexOptions.IgnoreCase);

    /// <summary>Extrai ISSNs rotulados (ex.: "ISSN: 2179-3786"). Exige a palavra "ISSN" por perto para não confundir com intervalos de ano ("2019-2024").</summary>
    public static HashSet<string> IssnNumbers(string text) =>
        CaptureGroups(text, IssnRegex).Select(s => s.Replace(" ", "").ToUpperInvariant()).ToHashSet();

    private static readonly Regex DoiRegex = new(@"(10\.\d{4,}/[^\s,;]+)", RegexOptions.IgnoreCase);

    /// <summary>Extrai DOIs (ex.: "10.1234/abc").</summary>
    public static HashSet<string> DoiNumbers(string text) =>
        CaptureGroups(text, DoiRegex).Select(s => s.ToLowerInvariant()).ToHashSet();

    private static IEnumerable<string> CaptureGroups(string text, Regex regex)
    {
        foreach (Match m in regex.Matches(text))
        {
            if (m.Groups.Count > 1 && m.Groups[1].Success) yield return m.Groups[1].Value;
        }
    }

    /// <summary>Palavra capaz de identificar (≥4 letras, não stopword, não puramente numérica).</summary>
    private static bool IsMeaningful(string token) =>
        token.Length >= 4 && !Stopwords.Contains(token) && !token.All(char.IsDigit);

    private static readonly Regex NonAlphanumeric = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    public static string Normalize(string text)
    {
        var folded = TextNormalization.FoldDiacriticsLower(text);
        var tokens = NonAlphanumeric.Split(folded).Where(t => t.Length > 0);
        return string.Join(" ", tokens);
    }

    /// <summary>Maior trecho contíguo de palavras do título presente no certificado (normalizado). Usa TODAS as palavras (inclusive preposições), pois o certificado também as tem.</summary>
    private static double PhraseFraction(IReadOnlyList<string> words, string cert)
    {
        if (words.Count < 2) return 0;
        var maxLen = Math.Min(words.Count, 10);
        var best = 0;
        for (var start = 0; start < words.Count; start++)
        {
            var len = Math.Min(maxLen, words.Count - start);
            while (len >= 2)
            {
                var phrase = string.Join(" ", words.Skip(start).Take(len));
                if (cert.Contains(phrase, StringComparison.Ordinal)) { best = Math.Max(best, len); break; }
                len--;
            }
            if (best == maxLen) break;
        }
        return (double)best / maxLen;
    }
}
