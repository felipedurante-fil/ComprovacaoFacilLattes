using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ComprovacaoFacilLattes.Core.Text;

namespace ComprovacaoFacilLattes.Core.Qualis;

/// <summary>
/// Classificação Qualis (CAPES) de periódicos por quadriênio e área de avaliação. As
/// tabelas (2016-2019, 2017-2020, 2021-2024) vêm embutidas no assembly (mesmos
/// arquivos <c>.tsv.gz</c> já empacotados no app macOS) e são indexadas sob demanda
/// para a área escolhida pelo usuário.
///
/// Adaptação do port: a descompressão gzip do app original usava o framework
/// <c>Compression</c> da Apple com um parser manual de cabeçalho — aqui usamos
/// <see cref="GZipStream"/>, biblioteca padrão do .NET, sem precisar reescrever nada
/// disso.
/// </summary>
public sealed class QualisService
{
    private static readonly string[] Quads = { "2016_2019", "2017_2020", "2021_2024" };

    private sealed class QuadIndex
    {
        public Dictionary<string, string> ByIssn { get; } = new();
        public Dictionary<string, string> ByTitle { get; } = new();
        public List<(HashSet<string> Tokens, string Estrato)> Fuzzy { get; } = new();
    }

    private Dictionary<string, QuadIndex> _cache = new(); // "quad|area" -> índice
    private Dictionary<string, string> _titleToIssn = new(); // título normalizado -> ISSN (todas as áreas/quadriênios)

    public bool IsLoading { get; private set; }
    public IReadOnlyList<string> AllAreas { get; private set; } = Array.Empty<string>();

    private string _area = "FILOSOFIA";

    /// <summary>Área de avaliação selecionada.</summary>
    public string Area
    {
        get => _area;
        set
        {
            if (_area == value) return;
            _area = value;
            Reload();
        }
    }

    public sealed record Result(string Estrato, string Quadriennium, string Area);

    // MARK: - Carregamento

    public void Start() => Reload();

    /// <summary>
    /// Recarrega os índices. Síncrono: as tabelas já vêm embutidas, e a
    /// descompressão + parsing de ~170 mil linhas por quadriênio leva bem menos de um
    /// segundo — chamadores de UI podem envolver em <c>Task.Run</c> se quiserem manter
    /// a thread de interface livre durante a troca de área.
    /// </summary>
    public void Reload()
    {
        IsLoading = true;
        var built = new Dictionary<string, QuadIndex>();
        var areas = new HashSet<string>();
        var titleIssn = new Dictionary<string, string>();

        foreach (var quad in Quads)
        {
            var result = BuildIndex(quad, _area);
            if (result is null) continue;
            built[$"{quad}|{_area}"] = result.Value.Index;
            areas.UnionWith(result.Value.Areas);
            foreach (var (k, v) in result.Value.TitleIssn) titleIssn.TryAdd(k, v);
        }

        _cache = built;
        _titleToIssn = titleIssn;
        if (areas.Count > 0) AllAreas = areas.OrderBy(a => a, StringComparer.Ordinal).ToList();
        IsLoading = false;
    }

    // MARK: - Classificação

    /// <summary>Classifica um periódico pelo ISSN (preferencial), título e ano de publicação.</summary>
    public Result? Classify(string venue, string issn, int year)
    {
        var quad = QuadKey(year);
        if (!_cache.TryGetValue($"{quad}|{_area}", out var idx)) return null;

        // 1) ISSN exato
        var issnKey = NormIssn(issn);
        if (issnKey.Length > 0 && idx.ByIssn.TryGetValue(issnKey, out var e1))
            return new Result(e1, Label(quad), _area);

        // 2) Título do periódico exato
        var journal = NormTitle(JournalName(venue));
        if (journal.Length > 0 && idx.ByTitle.TryGetValue(journal, out var e2))
            return new Result(e2, Label(quad), _area);

        // 2b) Resolve via ISSN cruzado (periódico renomeado entre quadriênios)
        if (journal.Length > 0 && _titleToIssn.TryGetValue(journal, out var crossIssn)
            && idx.ByIssn.TryGetValue(crossIssn, out var e3))
        {
            return new Result(e3, Label(quad), _area);
        }

        // 3) Aproximado por sobreposição de palavras
        var vTokens = NormTitle(venue).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3).ToHashSet();
        if (vTokens.Count < 2) return null;

        (double Cov, string Estrato)? best = null;
        foreach (var entry in idx.Fuzzy)
        {
            var inter = entry.Tokens.Intersect(vTokens).Count();
            if (inter < 2) continue;
            // cobertura em relação ao título do periódico (mais curto)
            var cov = (double)inter / Math.Min(entry.Tokens.Count, vTokens.Count);
            if (cov >= 0.8 && (best is null || cov > best.Value.Cov)) best = (cov, entry.Estrato);
        }
        return best is null ? null : new Result(best.Value.Estrato, Label(quad), _area);
    }

    // MARK: - Construção do índice

    private static (QuadIndex Index, HashSet<string> Areas, Dictionary<string, string> TitleIssn)? BuildIndex(
        string quad, string area)
    {
        var text = ReadEmbeddedTsvGz($"qualis_{quad}.tsv.gz");
        if (text is null) return null;

        var idx = new QuadIndex();
        var areas = new HashSet<string>();
        var titleIssn = new Dictionary<string, string>();

        foreach (var rawLine in text.Split('\n'))
        {
            if (rawLine.Length == 0) continue;
            var line = rawLine.TrimEnd('\r');
            var f = line.Split('\t');
            if (f.Length < 4) continue;
            var (issn, title, rowArea, estrato) = (f[0], f[1], f[2], f[3]);
            areas.Add(rowArea);
            // Mapa global título→ISSN (todas as áreas) para resolver renomeações
            if (title.Length > 0 && issn.Length > 0) titleIssn[title] = issn;
            if (rowArea != area || estrato.Length == 0) continue;
            if (issn.Length > 0) idx.ByIssn[issn] = estrato;
            if (title.Length > 0)
            {
                idx.ByTitle[title] = estrato;
                var toks = title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 3).ToHashSet();
                if (toks.Count >= 2) idx.Fuzzy.Add((toks, estrato));
            }
        }
        return (idx, areas, titleIssn);
    }

    private static string? ReadEmbeddedTsvGz(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.Ordinal));
        if (resourceName is null) return null;
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // MARK: - Helpers

    public static string QuadKey(int year)
    {
        if (year >= 2021) return "2021_2024";
        if (year >= 2017) return "2017_2020";
        if (year > 0) return "2016_2019";
        return "2021_2024";
    }

    private static string Label(string quad) => quad.Replace('_', '-');

    public static string NormIssn(string s) =>
        new(s.ToUpperInvariant().Where(c => c is (>= '0' and <= '9') or 'X').ToArray());

    public static string NormTitle(string s)
    {
        var folded = TextNormalization.FoldDiacriticsLower(s).ToUpperInvariant();
        var kept = new string(folded.Where(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or ' ').ToArray());
        var tokens = kept.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", tokens);
    }

    private static readonly Regex JournalNameCutRegex =
        new(@"[.,]?\s*(v\.|n\.|p\.|vol\.|\bv\s*\d)", RegexOptions.IgnoreCase);

    /// <summary>Extrai o nome do periódico do campo "venue", cortando volume/página/ano.</summary>
    private static string JournalName(string venue)
    {
        var m = JournalNameCutRegex.Match(venue);
        var s = m.Success ? venue[..m.Index] : venue;
        return s.Trim();
    }
}
