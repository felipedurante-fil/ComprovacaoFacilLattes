using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComprovacaoFacilLattes.App.Services;
using ComprovacaoFacilLattes.Core.Archiving;
using ComprovacaoFacilLattes.Core.Models;
using ComprovacaoFacilLattes.Core.Persistence;
using ComprovacaoFacilLattes.Core.Qualis;
using ComprovacaoFacilLattes.Infrastructure.PdfReport;
using Microsoft.EntityFrameworkCore;

namespace ComprovacaoFacilLattes.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;
    private AppDbContext? _db;

    public ObservableCollection<ProfileListItemViewModel> Profiles { get; } = new();
    public QualisService Qualis { get; } = new();

    [ObservableProperty] private ProfileListItemViewModel? _selectedProfileItem;
    [ObservableProperty] private LattesProfile? _currentProfile;
    [ObservableProperty] private ObservableCollection<SectionViewModel> _sections = new();
    [ObservableProperty] private EntryViewModel? _selectedEntry;
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showOnlyPending;
    [ObservableProperty] private ObservableCollection<string> _qualisAreas = new();
    [ObservableProperty] private string? _selectedQualisArea;

    public int ConfirmedCount => CurrentProfile?.ConfirmedCount ?? 0;
    public int SuggestedCount => CurrentProfile?.SuggestedCount ?? 0;
    public int PendingCount => CurrentProfile?.PendingCount ?? 0;
    public int TotalCount => CurrentProfile?.TotalEntries ?? 0;
    public double CoveragePercent => TotalCount == 0 ? 0 : 100.0 * ConfirmedCount / TotalCount;
    public int LimboCount => CurrentProfile?.LimboCertificates.Count() ?? 0;

    public event Action<ScanReviewViewModel>? ScanReviewRequested;
    public event Action<GenerateReportViewModel>? GenerateReportRequested;

    /// <summary>Construtor sem parâmetros — usado pelo previewer de design do Avalonia.</summary>
    public MainWindowViewModel() : this(new DialogService()) { }

    public MainWindowViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        Qualis.Area = "FILOSOFIA";
        Qualis.Start();
        QualisAreas = new ObservableCollection<string>(Qualis.AllAreas);
        SelectedQualisArea = Qualis.Area;
        LoadProfiles();
    }

    private void LoadProfiles()
    {
        using var db = AppDb.Create();
        Profiles.Clear();
        foreach (var p in db.Profiles.OrderBy(p => p.Name)) Profiles.Add(new ProfileListItemViewModel(p));
    }

    partial void OnSelectedProfileItemChanged(ProfileListItemViewModel? value)
    {
        if (value is null)
        {
            CurrentProfile = null;
            Sections = new ObservableCollection<SectionViewModel>();
            return;
        }
        OpenProfile(value.Id);
    }

    private void OpenProfile(Guid id)
    {
        _db?.Dispose();
        _db = AppDb.Create();
        CurrentProfile = _db.Profiles
            .Include(p => p.Sections).ThenInclude(s => s.Entries).ThenInclude(e => e.Certificates)
            .Include(p => p.Certificates)
            .Single(p => p.Id == id);
        RebuildSections();
    }

    private void RebuildSections()
    {
        var source = CurrentProfile?.SortedSections ?? Enumerable.Empty<LattesSection>();
        if (ShowOnlyPending) source = source.Where(s => s.PendingCount > 0 || s.SuggestedCount > 0);
        Sections = new ObservableCollection<SectionViewModel>(source.Select(s => new SectionViewModel(s)));
        OnPropertyChanged(nameof(ConfirmedCount));
        OnPropertyChanged(nameof(SuggestedCount));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CoveragePercent));
        OnPropertyChanged(nameof(LimboCount));
    }

    partial void OnShowOnlyPendingChanged(bool value) => RebuildSections();

    partial void OnSelectedEntryChanged(EntryViewModel? value) => UpdatePreview(value);

    partial void OnSelectedQualisAreaChanged(string? value)
    {
        if (value is not null) Qualis.Area = value;
    }

    private void UpdatePreview(EntryViewModel? entry)
    {
        PreviewImage = null;
        var cert = entry?.Model.ConfirmedCertificates.FirstOrDefault(c => c.Exists);
        if (cert is null) return;
        try
        {
            if (cert.IsPdf)
            {
                var png = PdfPagePreviewRenderer.RenderFirstPageAsPng(cert.FilePath);
                if (png is null) return;
                using var ms = new MemoryStream(png);
                PreviewImage = new Bitmap(ms);
            }
            else if (cert.IsImage)
            {
                using var stream = File.OpenRead(cert.FilePath);
                PreviewImage = new Bitmap(stream);
            }
        }
        catch
        {
            // preview é melhor-esforço — a entrada continua utilizável sem ele
        }
    }

    [RelayCommand]
    private async Task ImportNewProfileAsync()
    {
        var path = await _dialogs.PickPdfFileAsync("Importar novo currículo Lattes…");
        if (path is null) return;
        IsBusy = true;
        StatusMessage = "Importando…";
        try
        {
            var id = await Task.Run(() => ProfileImportService.ImportNewProfile(path));
            LoadProfiles();
            SelectedProfileItem = Profiles.FirstOrDefault(p => p.Id == id);
        }
        finally
        {
            IsBusy = false;
            StatusMessage = "";
        }
    }

    [RelayCommand]
    private async Task ImportBackupAsync()
    {
        var path = await _dialogs.PickZipFileAsync("Importar arquivo de comprovação…");
        if (path is null) return;
        IsBusy = true;
        StatusMessage = "Importando backup…";
        try
        {
            using var db = AppDb.Create();
            var profile = await Task.Run(() => ProfileArchiver.Import(path, db, Path.GetTempPath()));
            LoadProfiles();
            SelectedProfileItem = Profiles.FirstOrDefault(p => p.Id == profile.Id);
        }
        catch (ProfileArchiveException ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfileItem is null) return;
        using var db = AppDb.Create();
        var profile = db.Profiles.Find(SelectedProfileItem.Id);
        if (profile is null) return;
        db.Profiles.Remove(profile);
        db.SaveChanges();
        var wasSelected = SelectedProfileItem.Id == CurrentProfile?.Id;
        LoadProfiles();
        if (wasSelected)
        {
            CurrentProfile = null;
            Sections = new ObservableCollection<SectionViewModel>();
        }
    }

    [RelayCommand]
    private async Task ExportProfileAsync()
    {
        if (CurrentProfile is null) return;
        var path = await _dialogs.PickSaveLocationAsync("Exportar comprovação…", $"{CurrentProfile.Name}.zip", "zip");
        if (path is null) return;
        IsBusy = true;
        try
        {
            var profile = CurrentProfile;
            var zipTemp = await Task.Run(() => ProfileArchiver.Export(profile, includeFiles: true, Path.GetTempPath()));
            File.Copy(zipTemp, path, overwrite: true);
            File.Delete(zipTemp);
            StatusMessage = "Exportado.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ScanFolderAsync()
    {
        if (CurrentProfile is null) return;
        var folder = await _dialogs.PickFolderAsync("Escanear Pasta por Certificados");
        if (folder is null) return;
        IsBusy = true;
        StatusMessage = "Escaneando…";
        try
        {
            var profile = CurrentProfile;
            var results = await Task.Run(() => ScanService.ScanFolder(profile, folder));
            if (results.Count == 0)
            {
                StatusMessage = "Nenhum arquivo novo para escanear.";
                return;
            }
            OpenScanReview(results);
        }
        finally
        {
            IsBusy = false;
            StatusMessage = "";
        }
    }

    [RelayCommand]
    private void ReviewLimbo()
    {
        if (CurrentProfile is null) return;
        var results = ScanService.ReviewLimbo(CurrentProfile);
        if (results.Count > 0) OpenScanReview(results);
    }

    private void OpenScanReview(List<ScanResultItem> results)
    {
        var vm = new ScanReviewViewModel(CurrentProfile!, _db!, results);
        vm.Applied += RebuildSections;
        ScanReviewRequested?.Invoke(vm);
    }

    [RelayCommand]
    private void OpenGenerateReport()
    {
        if (CurrentProfile is null) return;
        var vm = new GenerateReportViewModel(CurrentProfile, Qualis, _dialogs);
        GenerateReportRequested?.Invoke(vm);
    }

    [RelayCommand]
    private void OpenAddDocument()
    {
        if (CurrentProfile is null || _db is null) return;
        var vm = new AddDocumentViewModel(CurrentProfile, _db, _dialogs);
        vm.Saved += RebuildSections;
        AddDocumentRequested?.Invoke(vm);
    }

    public event Action<AddDocumentViewModel>? AddDocumentRequested;
    public event Action<ManageCertificatesViewModel>? ManageCertificatesRequested;

    public void OpenManageCertificates(EntryViewModel entryVm)
    {
        if (_db is null) return;
        var vm = new ManageCertificatesViewModel(_db, entryVm.Model);
        vm.Changed += () => { entryVm.Refresh(); RebuildSections(); };
        ManageCertificatesRequested?.Invoke(vm);
    }

    [RelayCommand]
    private void DeleteManualDocument(EntryViewModel? entryVm)
    {
        if (entryVm is null || _db is null || !entryVm.IsManualDocument) return;
        ManualDocumentService.DeleteDocument(_db, entryVm.Model);
        RebuildSections();
    }

    [RelayCommand]
    private async Task UpdateLattesAsync()
    {
        if (CurrentProfile is null) return;
        var path = await _dialogs.PickPdfFileAsync("Atualizar Lattes…");
        if (path is null) return;
        var id = CurrentProfile.Id;
        IsBusy = true;
        StatusMessage = "Atualizando…";
        try
        {
            await Task.Run(() => ProfileImportService.UpdateProfile(id, path));
            OpenProfile(id);
        }
        finally
        {
            IsBusy = false;
            StatusMessage = "";
        }
    }
}
