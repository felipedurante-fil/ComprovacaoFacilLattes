using System.ComponentModel.DataAnnotations.Schema;

namespace ComprovacaoFacilLattes.Core.Models;

/// <summary>
/// Uma seção do currículo — "Artigos completos…", "Participação em bancas… - Mestrado", etc.
/// O parser SUBDIVIDE seções do Lattes em várias seções do app; <see cref="Title"/> já inclui
/// sufixos de subdivisão, ex. "Atuação profissional - UFAC - Disciplinas ministradas".
/// </summary>
public class LattesSection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public int Order { get; set; }

    public Guid? ProfileId { get; set; }
    public LattesProfile? Profile { get; set; }

    public List<LattesEntry> Entries { get; set; } = new();

    public LattesSection() { }

    public LattesSection(string title, int order)
    {
        Title = title;
        Order = order;
    }

    [NotMapped]
    public IEnumerable<LattesEntry> SortedEntries => Entries.OrderBy(e => e.Order);

    [NotMapped]
    public int ConfirmedCount => Entries.Count(e => e.CertificateStatus == EntryStatus.Confirmed);

    [NotMapped]
    public int SuggestedCount => Entries.Count(e => e.CertificateStatus == EntryStatus.Suggested);

    [NotMapped]
    public int PendingCount => Entries.Count(e => e.CertificateStatus == EntryStatus.None);
}
