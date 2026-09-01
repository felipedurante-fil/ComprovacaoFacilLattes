using ComprovacaoFacilLattes.Core.Models;

namespace ComprovacaoFacilLattes.App.ViewModels;

public sealed class CertificateRowViewModel : ViewModelBase
{
    public Certificate Model { get; }

    public CertificateRowViewModel(Certificate model) => Model = model;

    public string FileName => Model.FileName;
    public bool IsConfirmed => Model.IsConfirmed;

    public void Refresh() => OnPropertyChanged(nameof(IsConfirmed));
}
