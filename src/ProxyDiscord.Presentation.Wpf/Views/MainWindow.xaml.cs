using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProxyDiscord.Presentation.Wpf.Shell;
using ProxyDiscord.Presentation.Wpf.ViewModels;

namespace ProxyDiscord.Presentation.Wpf.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _allowClose;

    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        if (AppIcons.TryLoadAppIcon() is { } icon)
        {
            Icon = icon;
        }

        Loaded += async (_, _) =>
        {
            await _viewModel.InitializeAsync();
            await _viewModel.VpnGateList.LoadCommand.ExecuteAsync(null);
        };
        Closed += (_, _) => _viewModel.Dispose();
    }

    // Libera o fechamento de verdade. Sem isso o OnClosing abaixo cancelaria também o encerramento
    // pedido pelo menu da bandeja, e o app ficaria impossível de fechar.
    public void AllowClose() => _allowClose = true;

    // O X manda a janela para a área de notificação em vez de encerrar — o túnel continua de pé.
    // Minimizar não passa por aqui: é o minimizar padrão do Windows, para a barra de tarefas.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void VpnGateRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: VpnGateServerRowViewModel row })
        {
            _viewModel.VpnGateList.SelectServerCommand.Execute(row);
        }
    }

    private void VpnGateColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Tag: string tag } &&
            Enum.TryParse<VpnGateSortColumn>(tag, out var column))
        {
            _viewModel.VpnGateList.SortByColumnCommand.Execute(column);
        }
    }
}
