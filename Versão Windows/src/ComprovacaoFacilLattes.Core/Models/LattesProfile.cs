using System.ComponentModel.DataAnnotations.Schema;

namespace ComprovacaoFacilLattes.Core.Models;

/// <summary>Um currículo/perfil — o app suporta vários em paralelo.</summary>
public class LattesProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string PdfPath { get; set; } = "";
    public DateTime ImportDate { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>Pasta padrão sugerida para salvar o relatório e para onde comprovantes importados são copiados.</summary>
    public string SavePath { get; set; } = "";

    /// <summary>Texto completo extraído do PDF (cache, evita reabrir o PDF para exibir resumo).</summary>
    public string RawText { get; set; } = "";

    /// <summary>
    /// Aprendizado: <c>"nomeArquivoSemExtensao||hashKeyDaEntrada"</c> — vínculos que o usuário já
    /// recusou explicitamente, para não sugerir de novo.
    /// </summary>
    public List<string> RejectedLinks { get; set; } = new();

    public List<LattesSection> Sections { get; set; } = new();

    /// <summary>TODOS os certificados do perfil (vinculados + em limbo).</summary>
    public List<Certificate> Certificates { get; set; } = new();

    public LattesProfile() { }

    public LattesProfile(string name, string pdfPath, string savePath)
    {
        Name = name;
        PdfPath = pdfPath;
        SavePath = savePath;
    }

    [NotMapped]
    public IEnumerable<LattesSection> SortedSections => Sections.OrderBy(s => s.Order);

    [NotMapped]
    public int TotalEntries => Sections.Sum(s => s.Entries.Count);

    [NotMapped]
    public int ConfirmedCount => Sections.Sum(s => s.Entries.Count(e => e.CertificateStatus == EntryStatus.Confirmed));

    [NotMapped]
    public int SuggestedCount => Sections.Sum(s => s.Entries.Count(e => e.CertificateStatus == EntryStatus.Suggested));

    [NotMapped]
    public int PendingCount => Sections.Sum(s => s.Entries.Count(e => e.CertificateStatus == EntryStatus.None));

    /// <summary>Certificados escaneados sem vínculo encontrado, ou vínculo desfeito.</summary>
    [NotMapped]
    public IEnumerable<Certificate> LimboCertificates => Certificates.Where(c => c.Entry == null && !c.IsRejected);
}
