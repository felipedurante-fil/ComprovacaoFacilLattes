using ComprovacaoFacilLattes.Core.Models;

namespace ComprovacaoFacilLattes.App.ViewModels;

public sealed class ProfileListItemViewModel : ViewModelBase
{
    public Guid Id { get; }
    public string Name { get; }

    public ProfileListItemViewModel(LattesProfile profile)
    {
        Id = profile.Id;
        Name = profile.Name;
    }
}
