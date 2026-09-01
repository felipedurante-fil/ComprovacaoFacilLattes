using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComprovacaoFacilLattes.App.Services;
using ComprovacaoFacilLattes.Core.Models;
using ComprovacaoFacilLattes.Core.Qualis;
using ComprovacaoFacilLattes.Core.Reporting;

namespace ComprovacaoFacilLattes.App.ViewModels;

public sealed class ReportSectionOption : ViewModelBase
{
    public string Title { get; }
    private bool _isSelected = true;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public ReportSectionOption(string title) => Title = title;
}

public sealed partial class GenerateReportViewModel : ViewModelBase
{
    private readonly LattesProfile _profile;
    private readonly QualisService _qualis;
    private readonly IDialogService _dialogs;

    public ObservableCollection<ReportSectionOption> SectionOptions { get; }

    [ObservableProperty] private int? _startYear;
    [ObservableProperty] private int? _endYear;
    [ObservableProperty] private bool _includeLattes = true;
    [ObservableProperty] private bool _includeToc = true;
    [ObservableProperty] private bool _numberPages = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";

    public event Action? RequestClose;

    public GenerateReportViewModel(LattesProfile profile, QualisService qualis, IDialogService dialogs)
    {
        _profile = profile;
        _qualis = qualis;
        _dialogs = dialogs;
        SectionOptions = new ObservableCollection<ReportSectionOption>(
            profile.SortedSections.Select(s => new ReportSectionOption(s.Title)));
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        var selected = SectionOptions.Where(o => o.IsSelected).Select(o => o.Title).ToHashSet();
        if (selected.Count == 0) { StatusMessage = "Selecione ao menos uma seção."; return; }

        var savePath = await _dialogs.PickSaveLocationAsync(
            "Salvar relatório", $"{_profile.Name} - Comprovantes.pdf", "pdf");
        if (savePath is null) return;

        IsBusy = true;
        StatusMessage = "Gerando…";
        try
        {
            var config = new ReportConfig
            {
                Profile = _profile,
                SelectedSectionTitles = selected,
                StartYear = StartYear,
                EndYear = EndYear,
                IncludeLattes = IncludeLattes,
                IncludeToc = IncludeToc,
                NumberPages = NumberPages,
            };
            var bytes = await Task.Run(() => ReportService.Generate(_profile, config, _qualis));
            if (bytes is null)
            {
                StatusMessage = "Nada para incluir com os filtros escolhidos.";
                return;
            }
            await File.WriteAllBytesAsync(savePath, bytes);
            StatusMessage = "Relatório gerado.";
            RequestClose?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();
}
