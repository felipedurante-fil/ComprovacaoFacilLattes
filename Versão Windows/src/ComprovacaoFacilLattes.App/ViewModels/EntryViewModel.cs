using ComprovacaoFacilLattes.Core.Models;

namespace ComprovacaoFacilLattes.App.ViewModels;

public sealed class EntryViewModel : ViewModelBase
{
    public LattesEntry Model { get; }

    public EntryViewModel(LattesEntry model)
    {
        Model = model;
    }

    public string DisplayTitle => Model.DisplayTitle;
    public EntryStatus Status => Model.CertificateStatus;
    public int AttachmentCount => Model.Certificates.Count;
    public bool IsArticle => Model.Kind == "Artigo";
    public bool IsManualDocument => Model.Section?.Title == "Outros Documentos";

    public string StatusGlyph => Status switch
    {
        EntryStatus.Confirmed => "🟢",
        EntryStatus.Suggested => "🟡",
        _ => "🔴",
    };

    public void Refresh()
    {
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(AttachmentCount));
    }
}
