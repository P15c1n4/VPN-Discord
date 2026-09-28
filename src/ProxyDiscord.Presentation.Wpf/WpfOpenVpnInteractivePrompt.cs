using System.Windows;
using System.Windows.Threading;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Presentation.Wpf.Views;

namespace ProxyDiscord.Presentation.Wpf;

internal sealed class WpfOpenVpnInteractivePrompt(Dispatcher dispatcher) : IOpenVpnInteractivePrompt
{
    public async Task<OpenVpnPromptResponse?> RequestAsync(
        OpenVpnPromptRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = new OpenVpnInteractiveCredentialWindow(request);
            using var cancellationRegistration = cancellationToken.Register(() =>
                dispatcher.BeginInvoke(new Action(window.Close)));
            if (System.Windows.Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, window))
            {
                window.Owner = owner;
            }

            return window.ShowDialog() == true ? window.Response : null;
        });
        return await operation.Task;
    }
}
