using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using ComprovacaoFacilLattes.App.Services;
using ComprovacaoFacilLattes.Core.Models;
using ComprovacaoFacilLattes.Core.Persistence;

namespace ComprovacaoFacilLattes.App.ViewModels;

/// <summary>Lista os comprovantes de uma entrada — confirmar/desconfirmar, excluir, reordenar por setas (equivalente ao arrastar-e-soltar do app original).</summary>
public sealed class ManageCertificatesViewModel : ViewModelBase
{
    private readonly AppDbContext _db;
    private readonly LattesEntry _entry;

    public ObservableCollection<CertificateRowViewModel> Certificates { get; }
    public string EntryTitle => _entry.DisplayTitle;

    public event Action? Changed;

    public ManageCertificatesViewModel(AppDbContext db, LattesEntry entry)
    {
        _db = db;
        _entry = entry;
        Certificates = new ObservableCollection<CertificateRowViewModel>(
            entry.SortedCertificates.Select(c => new CertificateRowViewModel(c)));
    }

    public IRelayCommand<CertificateRowViewModel> ToggleConfirmCommand => new RelayCommand<CertificateRowViewModel>(Toggle);
    public IRelayCommand<CertificateRowViewModel> DeleteCommand => new RelayCommand<CertificateRowViewModel>(Delete);
    public IRelayCommand<CertificateRowViewModel> MoveUpCommand => new RelayCommand<CertificateRowViewModel>(r => Move(r, -1));
    public IRelayCommand<CertificateRowViewModel> MoveDownCommand => new RelayCommand<CertificateRowViewModel>(r => Move(r, 1));
    public IRelayCommand ConfirmAllCommand => new RelayCommand(ConfirmAll);

    private void Toggle(CertificateRowViewModel? row)
    {
        if (row is null) return;
        row.Model.IsConfirmed = !row.Model.IsConfirmed;
        ScanService.RefreshStatus(_entry);
        _db.SaveChanges();
        row.Refresh();
        Changed?.Invoke();
    }

    private void ConfirmAll()
    {
        foreach (var row in Certificates) row.Model.IsConfirmed = true;
        ScanService.RefreshStatus(_entry);
        _db.SaveChanges();
        foreach (var row in Certificates) row.Refresh();
        Changed?.Invoke();
    }

    private void Delete(CertificateRowViewModel? row)
    {
        if (row is null) return;
        _entry.Certificates.Remove(row.Model);
        _db.Certificates.Remove(row.Model);
        ScanService.RefreshStatus(_entry);
        _db.SaveChanges();
        Certificates.Remove(row);
        Changed?.Invoke();
    }

    private void Move(CertificateRowViewModel? row, int delta)
    {
        if (row is null) return;
        var idx = Certificates.IndexOf(row);
        var newIdx = idx + delta;
        if (newIdx < 0 || newIdx >= Certificates.Count) return;
        Certificates.Move(idx, newIdx);
        for (var i = 0; i < Certificates.Count; i++) Certificates[i].Model.Order = i;
        _db.SaveChanges();
        Changed?.Invoke();
    }
}
