using System.Collections.ObjectModel;
using ComprovacaoFacilLattes.Core.Models;

namespace ComprovacaoFacilLattes.App.ViewModels;

public sealed class SectionViewModel : ViewModelBase
{
    public LattesSection Model { get; }
    public ObservableCollection<EntryViewModel> Entries { get; }

    public SectionViewModel(LattesSection model)
    {
        Model = model;
        Entries = new ObservableCollection<EntryViewModel>(model.SortedEntries.Select(e => new EntryViewModel(e)));
    }

    public string Title => Model.Title;
    public int ConfirmedCount => Model.ConfirmedCount;
    public int PendingCount => Model.PendingCount;
    public int TotalCount => Entries.Count;

    public bool HasPending => PendingCount > 0 || Model.SuggestedCount > 0;
}
