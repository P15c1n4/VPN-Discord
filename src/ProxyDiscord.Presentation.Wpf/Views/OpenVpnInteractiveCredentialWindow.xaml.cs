using System.Windows;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Presentation.Wpf.Views;

public partial class OpenVpnInteractiveCredentialWindow : Window
{
    private readonly OpenVpnPromptRequest _request;

    public OpenVpnInteractiveCredentialWindow(OpenVpnPromptRequest request)
    {
        InitializeComponent();
        _request = request;
        MessageText.Text = request.Message;
        if (request.Kind == OpenVpnPromptKind.PrivateKeyPassphrase)
        {
            UsernamePanel.Visibility = Visibility.Collapsed;
            ChallengePanel.Visibility = Visibility.Collapsed;
            PasswordLabel.Text = "Senha da chave privada";
        }
        else
        {
            UsernameInput.Text = request.Username ?? string.Empty;
            PasswordInput.Password = request.Password ?? string.Empty;
            ChallengeLabel.Text = request.Message;
            MessageText.Text = "O servidor solicitou uma etapa adicional de autenticação.";
            if (request.ChallengeResponseEcho)
            {
                ChallengePasswordInput.Visibility = Visibility.Collapsed;
                ChallengeTextInput.Visibility = Visibility.Visible;
            }
        }
    }

    public OpenVpnPromptResponse? Response { get; private set; }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_request.Kind == OpenVpnPromptKind.PrivateKeyPassphrase)
        {
            Response = new OpenVpnPromptResponse(null, PasswordInput.Password, null);
        }
        else
        {
            var challengeResponse = _request.ChallengeResponseEcho
                ? ChallengeTextInput.Text
                : ChallengePasswordInput.Password;
            Response = new OpenVpnPromptResponse(
                UsernameInput.Text,
                PasswordInput.Password,
                challengeResponse);
        }

        DialogResult = true;
    }
}
