using Avalonia.Controls;
using ComprovacaoFacilLattes.App.ViewModels;

namespace ComprovacaoFacilLattes.App.Views;

public partial class GenerateReportWindow : Window
{
    public GenerateReportWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is GenerateReportViewModel vm) vm.RequestClose += Close;
        };
    }
}
