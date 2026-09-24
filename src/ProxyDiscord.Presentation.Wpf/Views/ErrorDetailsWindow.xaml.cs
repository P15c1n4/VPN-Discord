using System.Runtime.InteropServices;
using System.Windows;

namespace ProxyDiscord.Presentation.Wpf.Views;

public partial class ErrorDetailsWindow : Window
{
    public ErrorDetailsWindow(string errorMessage)
    {
        InitializeComponent();
        ErrorTextBox.Text = errorMessage;
        ErrorTextBox.CaretIndex = 0;
    }

    private void CopyError_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ErrorTextBox.Text);
            CopyButton.Content = "Copiado";
        }
        catch (ExternalException)
        {
            CopyButton.Content = "Falha ao copiar";
        }
    }
}
