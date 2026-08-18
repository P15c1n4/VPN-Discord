using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProxyDiscord.Domain.Entities;
using ProxyDiscord.Presentation.Wpf.ViewModels;

namespace ProxyDiscord.Presentation.Wpf.Views;

public partial class ProcessPickerWindow : Window
{
    private readonly ProcessPickerViewModel _viewModel;

    public ProcessPickerWindow(ProcessPickerViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await _viewModel.LoadCommand.ExecuteAsync(null);
    }

    public ProcessInfo? SelectedProcess { get; private set; }

    private void SelectButton_Click(object sender, RoutedEventArgs e) => TryAcceptSelection();

    private void ProcessTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _viewModel.SelectedNode = e.NewValue;

    private void ProcessTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<TreeViewItem>(source) is not null)
        {
            TryAcceptSelection();
        }
    }

    private void TryAcceptSelection()
    {
        if (_viewModel.SelectedProcess is not { } process)
        {
            return;
        }

        SelectedProcess = process;
        DialogResult = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
