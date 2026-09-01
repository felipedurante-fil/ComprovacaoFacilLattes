using ComprovacaoFacilLattes.Core.Models;

namespace ComprovacaoFacilLattes.Core.Reporting;

public sealed class ReportConfig
{
    public required LattesProfile Profile { get; init; }

    /// <summary>Vazio = todas as seções.</summary>
    public HashSet<string> SelectedSectionTitles { get; init; } = new();

    public int? StartYear { get; init; }
    public int? EndYear { get; init; }
    public bool IncludeLattes { get; init; } = true;

    /// <summary>Id da entrada → estrato Qualis (ex.: "A1"). Pré-calculado antes de gerar.</summary>
    public Dictionary<Guid, string> QualisByEntry { get; init; } = new();

    public bool IncludeToc { get; init; } = true;
    public bool NumberPages { get; init; } = true;
}

/// <summary>
/// Conta páginas de um PDF externo (Lattes completo ou comprovante) — a única
/// dependência não-portável do planejamento, injetada para manter
/// <see cref="ReportPlanner"/> livre de qualquer biblioteca de PDF concreta.
/// Implementações devem devolver 0 (nunca lançar) quando o arquivo não existir ou
/// não puder ser aberto — o planejador trata isso como "sem páginas para incluir",
/// igual ao comportamento do app original ao falhar em abrir um PDF externo.
/// </summary>
public interface IPdfPageCounter
{
    int GetPageCount(string pdfPath);
}
