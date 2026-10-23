using System.Windows;
using System.Windows.Controls;

namespace ProxyDiscord.Presentation.Wpf.Views;

public partial class PasswordField : UserControl
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.Register(
        nameof(Password), typeof(string), typeof(PasswordField),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (source, _) => ((PasswordField)source).SyncInputs()));

    private bool _updating;
    public bool IsReadOnly { get; set; }
    public event RoutedEventHandler? PasswordChanged;
    public string Password { get => (string)GetValue(PasswordProperty); set => SetValue(PasswordProperty, value ?? ""); }
    public bool IsRevealed => VisibleInput.Visibility == Visibility.Visible;

    public PasswordField()
    {
        InitializeComponent();
        Loaded += (_, _) => { HiddenInput.IsEnabled = !IsReadOnly; VisibleInput.IsReadOnly = IsReadOnly; };
        DataObject.AddPastingHandler(this, (_, e) => { if (IsReadOnly) e.CancelCommand(); else SelectAll(); });
        Unloaded += (_, _) => HidePassword();
    }

    private void SyncInputs()
    {
        if (HiddenInput is null || VisibleInput is null) return;
        _updating = true;
        try
        {
            if (HiddenInput.Password != Password) HiddenInput.Password = Password;
            if (VisibleInput.Text != Password) VisibleInput.Text = Password;
        }
        finally { _updating = false; }
    }

    private void HiddenInput_Changed(object sender, RoutedEventArgs e) => ChangeFromUser(HiddenInput.Password);
    private void VisibleInput_Changed(object sender, TextChangedEventArgs e) => ChangeFromUser(VisibleInput.Text);
    private void ChangeFromUser(string value)
    {
        if (_updating) return;
        SetCurrentValue(PasswordProperty, value);
        PasswordChanged?.Invoke(this, new RoutedEventArgs());
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (IsRevealed) HidePassword();
        else
        {
            HiddenInput.Visibility = Visibility.Collapsed;
            VisibleInput.Visibility = Visibility.Visible;
            RevealButton.Content = "Ocultar";
            VisibleInput.Focus();
            VisibleInput.CaretIndex = VisibleInput.Text.Length;
        }
    }

    public void HidePassword()
    {
        HiddenInput.Visibility = Visibility.Visible;
        VisibleInput.Visibility = Visibility.Collapsed;
        RevealButton.Content = "Mostrar";
    }

    public void SelectAll()
    {
        if (IsRevealed) VisibleInput.SelectAll();
        else HiddenInput.SelectAll();
    }
}
