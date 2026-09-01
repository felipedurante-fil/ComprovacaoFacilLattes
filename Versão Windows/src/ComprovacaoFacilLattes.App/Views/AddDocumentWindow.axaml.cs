using Avalonia.Controls;
using ComprovacaoFacilLattes.App.ViewModels;

namespace ComprovacaoFacilLattes.App.Views;

public partial class AddDocumentWindow : Window
{
    public AddDocumentWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AddDocumentViewModel vm) vm.RequestClose += Close;
        };
    }
}
