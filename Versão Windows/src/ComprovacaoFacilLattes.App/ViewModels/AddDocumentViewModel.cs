using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComprovacaoFacilLattes.App.Services;
using ComprovacaoFacilLattes.Core.Models;
using ComprovacaoFacilLattes.Core.Persistence;

namespace ComprovacaoFacilLattes.App.ViewModels;

public sealed partial class AddDocumentViewModel : ViewModelBase
{
    private readonly LattesProfile _profile;
    private readonly AppDbContext _db;
    private readonly IDialogService _dialogs;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string _statusMessage = "";

    public event Action? RequestClose;
    public event Action? Saved;

    public AddDocumentViewModel(LattesProfile profile, AppDbContext db, IDialogService dialogs)
    {
        _profile = profile;
        _db = db;
        _dialogs = dialogs;
    }

    [RelayCommand]
    private async Task PickFileAsync()
    {
        var path = await _dialogs.PickAnyFileAsync("Selecionar arquivo…");
        if (path is not null) FilePath = path;
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Title) || FilePath is null)
        {
            StatusMessage = "Informe um título e escolha um arquivo.";
            return;
        }
        ManualDocumentService.AddDocument(_db, _profile, Title.Trim(), FilePath);
        Saved?.Invoke();
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();
}
