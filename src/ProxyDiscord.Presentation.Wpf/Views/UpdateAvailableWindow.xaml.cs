using System.Windows;
using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Presentation.Wpf.Views;

public partial class UpdateAvailableWindow : Window
{
    public UpdateAvailableWindow(Version currentVersion, UpdateReleaseInfo release)
    {
        InitializeComponent();
        VersionText.Text = $"Instalada: {currentVersion}    ·    Nova versão: {release.TagName}";
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(release.ReleaseNotes)
            ? "Não há notas para esta versão."
            : release.ReleaseNotes.Trim();
    }

    public bool UpdateAccepted { get; private set; }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        UpdateAccepted = true;
        DialogResult = true;
    }
}
