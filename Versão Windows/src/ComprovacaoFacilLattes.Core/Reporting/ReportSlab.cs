namespace ComprovacaoFacilLattes.Core.Reporting;

/// <summary>Uma página do documento final: conteúdo externo (PDF/imagem) ou desenhada pelo app.</summary>
public abstract class ReportSlab
{
    /// <summary><c>false</c> → a página é contada na numeração, mas o número não é impresso (divisórias de seção).</summary>
    public bool ShowsNumber { get; set; } = true;
}

/// <summary>Uma página copiada de um PDF externo (o Lattes completo, ou um comprovante em PDF).</summary>
public sealed class ExternalPageSlab : ReportSlab
{
    public string SourcePdfPath { get; set; } = "";

    /// <summary>Índice 0-based da página dentro do PDF de origem.</summary>
    public int PageIndex { get; set; }
}

/// <summary>Um comprovante em formato de imagem, desenhado centralizado numa página própria.</summary>
public sealed class ImageSlab : ReportSlab
{
    public string ImagePath { get; set; } = "";
}

/// <summary>Página divisória de seção (fundo colorido, título grande centralizado) — nunca mostra número.</summary>
public sealed class DividerSlab : ReportSlab
{
    public string Title { get; set; } = "";
    public DividerSlab() => ShowsNumber = false;
}

/// <summary>Página de cabeçalho de uma entrada (título, autores, badge Qualis se for artigo).</summary>
public sealed class EntryHeaderSlab : ReportSlab
{
    public string SectionTitle { get; set; } = "";
    public string? Qualis { get; set; }
    public string EntryDisplayTitle { get; set; } = "";
    public string Authors { get; set; } = "";
}

public sealed class TocLine
{
    public string Text { get; set; } = "";
    public int Page { get; set; }

    /// <summary>0 = seção, 1 = item.</summary>
    public int Level { get; set; }

    public bool IsSection { get; set; }
}

/// <summary>Uma página do sumário.</summary>
public sealed class TocPageSlab : ReportSlab
{
    /// <summary>Só a primeira página do sumário mostra o cabeçalho "Sumário".</summary>
    public bool ShowHeader { get; set; }

    public List<TocLine> Lines { get; set; } = new();
}
