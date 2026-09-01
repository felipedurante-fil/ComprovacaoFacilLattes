using System.ComponentModel.DataAnnotations.Schema;

namespace ComprovacaoFacilLattes.Core.Models;

/// <summary>Um arquivo de comprovante (certificado, portaria, diploma…).</summary>
public class Certificate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Caminho ABSOLUTO no disco — o app nunca move/copia os arquivos originais ao vincular (só ao importar um backup que embute os arquivos).</summary>
    public string FilePath { get; set; } = "";

    public string FileName { get; set; } = "";
    public string FileExtension { get; set; } = "";

    /// <summary>Texto extraído (nativo do PDF ou via OCR) — cacheado para não reprocessar.</summary>
    public string ExtractedText { get; set; } = "";

    /// <summary>0.0–1.0, score do matching automático (0 = vínculo manual).</summary>
    public double Confidence { get; set; }

    /// <summary>Usuário confirmou este vínculo específico.</summary>
    public bool IsConfirmed { get; set; }

    /// <summary>Usuário descartou este arquivo explicitamente (não deve reaparecer em buscas).</summary>
    public bool IsRejected { get; set; }

    public DateTime ImportDate { get; set; } = DateTime.UtcNow;

    /// <summary>Ordem entre os vários comprovantes de uma mesma entrada (controla ordem no PDF final).</summary>
    public int Order { get; set; }

    public Guid? ProfileId { get; set; }
    public LattesProfile? Profile { get; set; }

    /// <summary><c>null</c> = "em limbo".</summary>
    public Guid? EntryId { get; set; }
    public LattesEntry? Entry { get; set; }

    public Certificate() { }

    public Certificate(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        FileExtension = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
    }

    [NotMapped] public bool IsPdf => FileExtension == "pdf";

    [NotMapped]
    public bool IsImage => FileExtension is "jpg" or "jpeg" or "png" or "tiff" or "tif" or "heic";

    [NotMapped] public bool Exists => File.Exists(FilePath);

    [NotMapped] public string FileNameNoExt => Path.GetFileNameWithoutExtension(FileName);
}
