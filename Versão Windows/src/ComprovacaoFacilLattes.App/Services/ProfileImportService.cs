using ComprovacaoFacilLattes.Core.Models;
using ComprovacaoFacilLattes.Core.Parsing;
using ComprovacaoFacilLattes.Core.Persistence;
using ComprovacaoFacilLattes.Infrastructure.PdfText;
using Microsoft.EntityFrameworkCore;

namespace ComprovacaoFacilLattes.App.Services;

/// <summary>Importa um novo currículo Lattes (PDF) ou reprocessa um já existente ("Atualizar Lattes").</summary>
public static class ProfileImportService
{
    public static Guid ImportNewProfile(string pdfPath)
    {
        var text = PdfTextExtractor.ExtractFullText(pdfPath);
        var parsed = LattesPdfParser.Parse(text);
        var name = string.IsNullOrWhiteSpace(parsed.ProfileName) ? "Currículo Lattes" : parsed.ProfileName;

        using var db = AppDb.Create();
        var uniqueName = MakeUniqueName(name, db);
        var savePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ComprovantesLattes", uniqueName);
        Directory.CreateDirectory(savePath);

        var profile = new LattesProfile(uniqueName, pdfPath, savePath) { RawText = text };
        AddSections(profile, parsed);

        db.Profiles.Add(profile);
        db.SaveChanges();
        return profile.Id;
    }

    /// <summary>Reconstrói TODAS as seções a partir de um novo PDF, preservando a seção manual "Outros Documentos" e tentando re-vincular certificados já confirmados pelo hash da entrada.</summary>
    public static void UpdateProfile(Guid profileId, string newPdfPath)
    {
        using var db = AppDb.Create();
        var profile = db.Profiles
            .Include(p => p.Sections).ThenInclude(s => s.Entries).ThenInclude(e => e.Certificates)
            .Single(p => p.Id == profileId);

        var text = PdfTextExtractor.ExtractFullText(newPdfPath);
        var parsed = LattesPdfParser.Parse(text);

        var oldByHash = profile.Sections
            .Where(s => s.Title != "Outros Documentos")
            .SelectMany(s => s.Entries)
            .GroupBy(e => e.HashKey)
            .ToDictionary(g => g.Key, g => g.First());

        var manualSection = profile.Sections.FirstOrDefault(s => s.Title == "Outros Documentos");
        var toRemove = profile.Sections.Where(s => s.Title != "Outros Documentos").ToList();
        foreach (var s in toRemove)
        {
            db.Sections.Remove(s);
            profile.Sections.Remove(s);
        }

        profile.PdfPath = newPdfPath;
        profile.RawText = text;
        profile.LastUpdated = DateTime.UtcNow;
        AddSections(profile, parsed, startOrder: manualSection is null ? 0 : 1);

        // Re-vincula por hash exato os certificados já confirmados.
        foreach (var section in profile.Sections)
        {
            if (section == manualSection) continue;
            foreach (var entry in section.Entries)
            {
                if (!oldByHash.TryGetValue(entry.HashKey, out var oldEntry)) continue;
                foreach (var cert in oldEntry.Certificates.ToList())
                {
                    cert.Entry = entry;
                    // Ver comentário equivalente em ScanService.ApplyLink — evita duplicar
                    // na coleção em memória (o fixup do EF já sincroniza ao salvar).
                    if (!entry.Certificates.Contains(cert)) entry.Certificates.Add(cert);
                }
                ScanService.RefreshStatus(entry);
            }
        }

        db.SaveChanges();
    }

    private static void AddSections(LattesProfile profile, LattesPdfParser.ParseResult parsed, int startOrder = 0)
    {
        var order = startOrder;
        foreach (var (title, entries) in parsed.Sections)
        {
            var section = new LattesSection(title, order++);
            var entryOrder = 0;
            foreach (var pe in entries)
            {
                var entry = new LattesEntry(pe.RawText, pe.Title, pe.Kind, pe.Year, pe.Authors, pe.Venue, entryOrder++)
                {
                    Doi = pe.Doi, Isbn = pe.Isbn, Portaria = pe.Portaria, Issn = pe.Issn,
                    Edital = pe.Edital, EndYear = pe.EndYear,
                };
                section.Entries.Add(entry);
            }
            profile.Sections.Add(section);
        }
    }

    public static string MakeUniqueName(string baseName, AppDbContext db)
    {
        var existing = db.Profiles.Select(p => p.Name).ToHashSet();
        if (!existing.Contains(baseName)) return baseName;
        var n = 2;
        while (existing.Contains($"{baseName} ({n})")) n++;
        return $"{baseName} ({n})";
    }
}
