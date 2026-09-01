using ComprovacaoFacilLattes.App.Services;
using ComprovacaoFacilLattes.Core.Models;

namespace ComprovacaoFacilLattes.App.ViewModels;

public sealed class ScanItemViewModel : ViewModelBase
{
    public ScanResultItem Result { get; }

    public ScanItemViewModel(ScanResultItem result)
    {
        Result = result;
        SelectedEntry = result.SuggestedEntry;
    }

    public string FileName => Result.Certificate.FileName;
    public int ScorePercent => (int)Math.Round(Result.Score * 100);
    public bool Confident => Result.Confident;
    public string StatusText => !Result.HasText
        ? "Sem texto legível — vínculo manual"
        : Result.NoLikelyEntry
            ? "Sem correspondência"
            : Confident
                ? $"Sugestão ({ScorePercent}%)"
                : $"Palpite ({ScorePercent}%)";

    public LattesEntry? SelectedEntry { get; set; }

    /// <summary>Marcado para entrar na aplicação em lote de "Vincular sugestões ≥90%".</summary>
    public bool IsChecked { get; set; }

    public bool Applied { get; private set; }

    public void MarkApplied() => Applied = true;
}
