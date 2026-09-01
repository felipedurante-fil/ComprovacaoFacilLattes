using Avalonia.Controls;
using Avalonia.Interactivity;
using ComprovacaoFacilLattes.App.ViewModels;

namespace ComprovacaoFacilLattes.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.ScanReviewRequested += OnScanReviewRequested;
                vm.GenerateReportRequested += OnGenerateReportRequested;
                vm.AddDocumentRequested += OnAddDocumentRequested;
                vm.ManageCertificatesRequested += OnManageCertificatesRequested;
            }
        };
    }

    private void OnEntryRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EntryViewModel entry } && DataContext is MainWindowViewModel vm)
        {
            vm.SelectedEntry = entry;
        }
    }

    private void OnManageEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EntryViewModel entry } && DataContext is MainWindowViewModel vm)
        {
            vm.OpenManageCertificates(entry);
        }
    }

    private void OnDeleteManualDocumentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EntryViewModel entry } && DataContext is MainWindowViewModel vm)
        {
            vm.DeleteManualDocumentCommand.Execute(entry);
        }
    }

    private async void OnScanReviewRequested(ScanReviewViewModel vm)
    {
        var window = new ScanReviewWindow { DataContext = vm };
        await window.ShowDialog(this);
    }

    private async void OnGenerateReportRequested(GenerateReportViewModel vm)
    {
        var window = new GenerateReportWindow { DataContext = vm };
        await window.ShowDialog(this);
    }

    private async void OnAddDocumentRequested(AddDocumentViewModel vm)
    {
        var window = new AddDocumentWindow { DataContext = vm };
        await window.ShowDialog(this);
    }

    private async void OnManageCertificatesRequested(ManageCertificatesViewModel vm)
    {
        var window = new ManageCertificatesWindow { DataContext = vm };
        await window.ShowDialog(this);
    }
}
