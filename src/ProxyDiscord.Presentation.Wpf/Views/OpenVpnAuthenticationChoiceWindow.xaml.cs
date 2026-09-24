using System.Windows;
using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Presentation.Wpf.Views;

public partial class OpenVpnAuthenticationChoiceWindow : Window
{
    public OpenVpnAuthenticationChoiceWindow(
        OpenVpnAuthenticationInfo authentication,
        bool localCredentialsAvailable)
    {
        InitializeComponent();
        SituationText.Text = DescribeSituation(authentication);
        ProfileButton.IsEnabled = authentication.ProfileOptionAvailable;
        LocalButton.IsEnabled = localCredentialsAvailable;
        AvailabilityText.Text = string.Join(Environment.NewLine, new[]
        {
            localCredentialsAvailable
                ? "Login local disponível."
                : "Para usar o login local, preencha usuário e senha na janela principal.",
            authentication.ProfileOptionAvailable
                ? "Autenticação pelo perfil disponível."
                : "O perfil não oferece um método de autenticação compatível."
        });
    }

    public OpenVpnCredentialSource? SelectedSource { get; private set; }

    private void UseProfile_Click(object sender, RoutedEventArgs e) => Select(OpenVpnCredentialSource.Profile);

    private void UseLocal_Click(object sender, RoutedEventArgs e) => Select(OpenVpnCredentialSource.Local);

    private void Select(OpenVpnCredentialSource source)
    {
        SelectedSource = source;
        DialogResult = true;
    }

    private static string DescribeSituation(OpenVpnAuthenticationInfo authentication) => authentication.Kind switch
    {
        OpenVpnAuthenticationKind.InlineCredentials when authentication.ProfileOptionAvailable =>
            "O perfil contém usuário e senha. Os valores permanecem ocultos.",
        OpenVpnAuthenticationKind.InlineCredentials =>
            "O bloco de autenticação do perfil está incompleto. O OpenVPN pediria o dado que falta pelo console, " +
            "indisponível no aplicativo.",
        OpenVpnAuthenticationKind.ExternalCredentialsFile when authentication.ProfileOptionAvailable =>
            "O perfil usa um arquivo externo com usuário e senha. Caminhos relativos partem da pasta do .ovpn.",
        OpenVpnAuthenticationKind.ExternalCredentialsFile =>
            "O arquivo de credenciais não foi localizado, não pôde ser lido ou não contém usuário e senha nas duas primeiras linhas.",
        OpenVpnAuthenticationKind.InteractivePrompt =>
            "O perfil pede usuário e senha durante a conexão, mas o OpenVPN é executado sem console. Use o login local.",
        OpenVpnAuthenticationKind.NoUsernamePasswordDirective when !authentication.ProfileOptionAvailable =>
            "O perfil não contém usuário e senha nem um método de autenticação compatível. Ajuste o perfil ou use o login local.",
        _ =>
            "O perfil não define usuário e senha. Pode usar certificado ou não exigir senha; não é possível confirmar " +
            "qual método o servidor exige."
    };
}
