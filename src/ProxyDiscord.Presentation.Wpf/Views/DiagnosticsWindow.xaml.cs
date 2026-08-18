using System.Windows;
using ProxyDiscord.Presentation.Wpf.ViewModels;

namespace ProxyDiscord.Presentation.Wpf.Views;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(DiagnosticsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        Loaded += (_, _) => viewModel.Refresh();
    }
}
