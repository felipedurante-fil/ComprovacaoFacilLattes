using ComprovacaoFacilLattes.Core.Models;
using ComprovacaoFacilLattes.Core.Persistence;

namespace ComprovacaoFacilLattes.App.Services;

/// <summary>Seção especial "Outros Documentos": entradas manuais (título livre + 1 arquivo), sempre por último, nunca reprocessada por "Atualizar Lattes".</summary>
public static class ManualDocumentService
{
    public const string SectionTitle = "Outros Documentos";
    private const int SectionOrder = int.MaxValue;

    public static void AddDocument(AppDbContext db, LattesProfile profile, string title, string filePath)
    {
        var section = profile.Sections.FirstOrDefault(s => s.Title == SectionTitle);
        if (section is null)
        {
            section = new LattesSection(SectionTitle, SectionOrder) { Profile = profile };
            db.Sections.Add(section);
            profile.Sections.Add(section);
        }

        var entry = new LattesEntry(title, title, kind: "Documento", order: section.Entries.Count) { Section = section };
        db.Entries.Add(entry);
        section.Entries.Add(entry);

        var cert = new Certificate(filePath) { IsConfirmed = true, Entry = entry, Profile = profile };
        db.Certificates.Add(cert);
        if (!entry.Certificates.Contains(cert)) entry.Certificates.Add(cert);

        entry.CertificateStatus = EntryStatus.Confirmed;
        db.SaveChanges();
    }

    /// <summary>Remove a entrada inteira — só faz sentido para entradas da seção manual (as demais vêm do parser e voltam ao reimportar/atualizar).</summary>
    public static void DeleteDocument(AppDbContext db, LattesEntry entry)
    {
        db.Entries.Remove(entry);
        db.SaveChanges();
    }
}
