using System.Windows;
using System.Windows.Controls;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.UseCases;

namespace ProxyDiscord.Presentation.Wpf.Views;

public partial class SavedCredentialsWindow : Window
{
    private readonly SavedCredentialsUseCase _credentials;
    private bool _busy;

    public SavedCredentialsWindow(SavedCredentialsUseCase credentials)
    {
        _credentials = credentials;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try { await ReloadAsync(); }
            catch (Exception ex) { StatusText.Text = $"Não foi possível carregar as credenciais. {ex.Message}"; }
        };
        Deactivated += (_, _) => SavedPassword.HidePassword();
        Closed += (_, _) => SavedPassword.Password = "";
    }

    private async Task ReloadAsync()
    {
        var entries = await _credentials.ListSavedAsync();
        EntriesGrid.ItemsSource = entries;
        SavedPassword.Password = "";
        SavedPassword.HidePassword();
        SavedPassword.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        ClearButton.IsEnabled = entries.Count != 0;
        StatusText.Text = entries.Count == 0 ? "Nenhuma credencial salva." : $"{entries.Count} servidor(es) com credenciais salvas.";
    }

    private async void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_busy) return;
        SavedPassword.Password = "";
        SavedPassword.HidePassword();
        SavedPassword.IsEnabled = DeleteButton.IsEnabled = false;
        if (EntriesGrid.SelectedItem is not SavedServerCredential entry) return;
        try
        {
            var credentials = await _credentials.ReadSavedAsync(entry.ServerKey);
            if (!ReferenceEquals(EntriesGrid.SelectedItem, entry)) return;
            SavedPassword.Password = credentials?.Password ?? "";
            SavedPassword.IsEnabled = credentials?.Password is not null;
            DeleteButton.IsEnabled = true;
        }
        catch (Exception ex) { StatusText.Text = $"Não foi possível carregar a credencial. {ex.Message}"; }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesGrid.SelectedItem is not SavedServerCredential entry) return;
        if (MessageBox.Show(this, $"Excluir a credencial de {entry.ServerKey}?", "Senhas salvas",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await ChangeAsync(() => _credentials.DeleteAsync(entry.ServerKey));
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Excluir todas as credenciais salvas?", "Senhas salvas",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await ChangeAsync(() => _credentials.ClearAsync());
    }

    private async Task ChangeAsync(Func<Task> change)
    {
        if (_busy) return;
        _busy = true;
        EntriesGrid.IsEnabled = DeleteButton.IsEnabled = ClearButton.IsEnabled = false;
        SavedPassword.Password = "";
        try { await change(); await ReloadAsync(); }
        catch (Exception ex) { StatusText.Text = $"Não foi possível atualizar as credenciais. {ex.Message}"; }
        finally
        {
            _busy = false;
            EntriesGrid.IsEnabled = true;
            DeleteButton.IsEnabled = EntriesGrid.SelectedItem is not null;
            ClearButton.IsEnabled = EntriesGrid.Items.Count != 0;
        }
    }
}
