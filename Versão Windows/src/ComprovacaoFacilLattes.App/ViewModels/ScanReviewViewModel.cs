using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using ComprovacaoFacilLattes.App.Services;
using ComprovacaoFacilLattes.Core.Models;
using ComprovacaoFacilLattes.Core.Persistence;

namespace ComprovacaoFacilLattes.App.ViewModels;

/// <summary>Tela de revisão de comprovantes escaneados — usa o MESMO <see cref="AppDbContext"/> do perfil aberto, para as entradas do combo já virem rastreadas por ele (evita conflitos ao anexar objetos entre contextos diferentes).</summary>
public sealed class ScanReviewViewModel : ViewModelBase
{
    private readonly LattesProfile _profile;
    private readonly AppDbContext _db;

    public ObservableCollection<ScanItemViewModel> Items { get; }
    public List<LattesEntry> AllEntries { get; }

    /// <summary>Disparado depois de qualquer vínculo aplicado, para o pai atualizar a árvore de seções.</summary>
    public event Action? Applied;

    public ScanReviewViewModel(LattesProfile profile, AppDbContext db, List<ScanResultItem> results)
    {
        _profile = profile;
        _db = db;
        AllEntries = profile.Sections.SelectMany(s => s.SortedEntries).ToList();
        Items = new ObservableCollection<ScanItemViewModel>(results.Select(r => new ScanItemViewModel(r)));
    }

    public IRelayCommand<ScanItemViewModel> LinkCommand => new RelayCommand<ScanItemViewModel>(Link);
    public IRelayCommand ApplyConfidentCommand => new RelayCommand(ApplyConfident);

    private void Link(ScanItemViewModel? item)
    {
        if (item?.SelectedEntry is null || item.Applied) return;
        ScanService.ApplyLink(_db, _profile, item.Result, item.SelectedEntry);
        item.MarkApplied();
        Applied?.Invoke();
    }

    private void ApplyConfident()
    {
        var any = false;
        foreach (var item in Items.Where(i => i.Confident && i.SelectedEntry is not null && !i.Applied))
        {
            ScanService.ApplyLink(_db, _profile, item.Result, item.SelectedEntry!);
            item.MarkApplied();
            any = true;
        }
        if (any) Applied?.Invoke();
    }
}
