using ComprovacaoFacilLattes.Core.Matching;
using ComprovacaoFacilLattes.Core.Models;
using ComprovacaoFacilLattes.Core.Persistence;
using ComprovacaoFacilLattes.Core.Scanning;
using ComprovacaoFacilLattes.Infrastructure.PdfText;

namespace ComprovacaoFacilLattes.App.Services;

/// <summary>Um arquivo escaneado e sua melhor correspondência (pode não haver).</summary>
public sealed class ScanResultItem
{
    public required Certificate Certificate { get; init; }
    public LattesEntry? SuggestedEntry { get; init; }
    public double Score { get; init; }
    public bool Confident { get; init; } // score >= 90% → sugestão confiável
    public bool HasText { get; init; }
    public bool NoLikelyEntry { get; init; } // tem texto mas nada correspondeu
}

/// <summary>Escaneia uma pasta, faz OCR/extração e sugere vínculos — orquestração de CertificateMatcher + CertificateTextExtractor + FileCollector.</summary>
public static class ScanService
{
    private const double SuggestThreshold = 0.90;
    private const double GuessFloor = 0.35;

    public static List<ScanResultItem> ScanFolder(LattesProfile profile, string folderPath, IProgress<string>? progress = null)
    {
        var entries = profile.Sections.SelectMany(s => s.SortedEntries).ToList();
        if (entries.Count == 0) return new List<ScanResultItem>();

        var entryFields = entries.Select(ToEntryFields).ToList();
        var idf = SimilarityMatcher.BuildIdf(entries.Select(e => e.Title));
        var rejected = profile.RejectedLinks.ToHashSet();
        var existingPaths = profile.Certificates.Select(c => c.FilePath).ToHashSet();

        var allFiles = FileCollector.CollectFiles(folderPath);
        var files = allFiles.Where(f => !existingPaths.Contains(f)).ToList();

        var rawItems = new List<(string FilePath, string Text, List<CertificateMatcher.ScoredMatch> Ranked, bool HasText)>();
        for (var i = 0; i < files.Count; i++)
        {
            var filePath = files[i];
            progress?.Report($"Lendo {i + 1} de {files.Count}: {Path.GetFileName(filePath)}");

            var extraction = CertificateTextExtractor.ExtractText(filePath);
            var hasText = extraction.Text.Length > 0;
            var baseName = Path.GetFileNameWithoutExtension(filePath);
            var nameText = baseName.Replace('_', ' ').Replace('-', ' ');
            var matchText = extraction.Text + " \n " + nameText;
            var certYears = CertificateMatcher.YearsIn(nameText);
            if (certYears.Count == 0) certYears = CertificateMatcher.YearsIn(extraction.Text);

            var relFolder = Path.GetDirectoryName(filePath) ?? "";
            var folderKinds = CertificateMatcher.InferFolderKinds(relFolder);

            var ranked = CertificateMatcher.RankedMatches(matchText, baseName, certYears, entryFields, folderKinds, idf, rejected);
            rawItems.Add((filePath, extraction.Text, ranked, hasText));
        }

        var chosen = CertificateMatcher.GlobalAssign(
            rawItems.Select(r => new CertificateMatcher.RankedItem(r.Ranked)).ToList(), GuessFloor);

        var results = new List<ScanResultItem>();
        for (var i = 0; i < rawItems.Count; i++)
        {
            var r = rawItems[i];
            var cert = new Certificate(r.FilePath) { ExtractedText = r.Text };
            var pick = chosen[i];
            CertificateMatcher.ScoredMatch? best = pick >= 0 && pick < r.Ranked.Count ? r.Ranked[pick] : null;
            var score = best?.Score ?? 0;
            cert.Confidence = score;
            var confident = score >= SuggestThreshold;
            var showGuess = score >= GuessFloor;

            results.Add(new ScanResultItem
            {
                Certificate = cert,
                SuggestedEntry = showGuess && best is not null ? entries[best.Value.Index] : null,
                Score = score,
                Confident = confident,
                HasText = r.HasText,
                NoLikelyEntry = r.HasText && !showGuess,
            });
        }
        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return results;
    }

    /// <summary>Rechecagem dos comprovantes órfãos (em limbo) — reaproveita o texto já extraído, sem reler os arquivos.</summary>
    public static List<ScanResultItem> ReviewLimbo(LattesProfile profile)
    {
        var entries = profile.Sections.SelectMany(s => s.SortedEntries).ToList();
        var limbo = profile.LimboCertificates.ToList();
        if (entries.Count == 0 || limbo.Count == 0) return new List<ScanResultItem>();

        var entryFields = entries.Select(ToEntryFields).ToList();
        var idf = SimilarityMatcher.BuildIdf(entries.Select(e => e.Title));
        var rejected = profile.RejectedLinks.ToHashSet();

        var results = new List<ScanResultItem>();
        foreach (var cert in limbo)
        {
            var baseName = cert.FileNameNoExt;
            var nameText = baseName.Replace('_', ' ').Replace('-', ' ');
            var hasText = cert.ExtractedText.Length > 0;
            var matchText = cert.ExtractedText + " \n " + nameText;
            var certYears = CertificateMatcher.YearsIn(nameText);
            if (certYears.Count == 0) certYears = CertificateMatcher.YearsIn(cert.ExtractedText);
            var relFolder = Path.GetDirectoryName(cert.FilePath) ?? "";
            var folderKinds = CertificateMatcher.InferFolderKinds(relFolder);

            var ranked = CertificateMatcher.RankedMatches(matchText, baseName, certYears, entryFields, folderKinds, idf, rejected);
            var best = ranked.Count > 0 ? ranked[0] : (CertificateMatcher.ScoredMatch?)null;
            var score = best?.Score ?? 0;
            cert.Confidence = score;
            var showGuess = score >= GuessFloor;

            results.Add(new ScanResultItem
            {
                Certificate = cert, SuggestedEntry = showGuess && best is not null ? entries[best.Value.Index] : null,
                Score = score, Confident = score >= SuggestThreshold, HasText = hasText, NoLikelyEntry = hasText && !showGuess,
            });
        }
        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return results;
    }

    private static CertificateMatcher.EntryFields ToEntryFields(LattesEntry e) => new()
    {
        Title = e.Title, Authors = e.Authors, Venue = e.Venue, Kind = e.Kind,
        Portaria = e.Portaria, Edital = e.Edital, Issn = e.Issn, Doi = e.Doi,
        Year = e.Year, EndYear = e.EndYear, HashKey = e.HashKey,
    };

    /// <summary>Confirma um vínculo: registra o certificado ligado à entrada e atualiza o status (verde).</summary>
    public static void ApplyLink(AppDbContext db, LattesProfile profile, ScanResultItem item, LattesEntry entry)
    {
        var cert = item.Certificate;
        var isNew = cert.Id == default || !db.Certificates.Any(c => c.Id == cert.Id);
        cert.Entry = entry;
        cert.Profile = profile;
        cert.IsConfirmed = true;
        cert.Order = entry.NextCertificateOrder();
        if (isNew) db.Certificates.Add(cert);
        // Setar cert.Entry já sincroniza entry.Certificates via fixup do EF Core ao
        // salvar — o Contains evita duplicar a entrada na lista em memória (RefreshStatus
        // e a UI leem essa coleção antes do SaveChanges rodar).
        if (!entry.Certificates.Contains(cert)) entry.Certificates.Add(cert);
        RefreshStatus(entry);
        db.SaveChanges();
    }

    /// <summary>Registra um arquivo escaneado sem vínculo — fica "em limbo" (sem entrada, sem confirmar).</summary>
    public static void SkipToLimbo(AppDbContext db, LattesProfile profile, ScanResultItem item)
    {
        item.Certificate.Profile = profile;
        db.Certificates.Add(item.Certificate);
        db.SaveChanges();
    }

    /// <summary>O status é armazenado, não recalculado automaticamente — precisa ser chamado sempre que os certificados de uma entrada mudam.</summary>
    public static void RefreshStatus(LattesEntry entry)
    {
        if (entry.Certificates.Any(c => c.IsConfirmed)) entry.CertificateStatus = EntryStatus.Confirmed;
        else if (entry.Certificates.Count > 0) entry.CertificateStatus = EntryStatus.Suggested;
        else entry.CertificateStatus = EntryStatus.None;
    }
}
