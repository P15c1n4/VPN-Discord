using System.IO;
using System.Windows;
using System.Windows.Controls;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Domain.ValueObjects;
using ProxyDiscord.Presentation.Wpf.ViewModels;
using ProxyDiscord.Presentation.Wpf.Views;
using Xunit;

namespace ProxyDiscord.Application.Tests;

public sealed class CredentialMisuseTests
{
    [Fact]
    public Task ChangedFileEndpointCannotAuthenticateOrSaveForAnotherServer() => StaTest.RunAsync(async () =>
    {
        using var env = new MisuseTestEnvironment();
        var path = await env.WriteProfileAsync("remote vpn.example 443\nauth-user-pass\n");
        using var vm = env.CreateViewModel(() => path);
        await vm.LoadOpenVpnProfileCommand.ExecuteAsync(null);
        vm.ServerHost = "other.example";
        vm.Username = "alice";
        vm.SetPasswordFromUser("secret");
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(0, env.OpenVpn.ConnectCount);
        Assert.Contains("não corresponde", vm.ErrorMessage);
        Assert.Empty(await env.Store.ListAsync());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CancelOrDisableSavingDuringDialDoesNotPersistPassword(bool cancel) => StaTest.RunAsync(async () =>
    {
        using var env = new MisuseTestEnvironment();
        using var vm = env.CreateViewModel();
        vm.SelectedProtocol = VpnProtocol.Sstp;
        await vm.SetSaveCredentialsEnabledAsync(true);
        await vm.SetSavePasswordAfterConnectionEnabledAsync(true);
        vm.Username = "alice";
        vm.SetPasswordFromUser("secret");
        env.Sstp.DialPause = new MisuseTestEnvironment.PausePoint();
        var connecting = vm.ConnectCommand.ExecuteAsync(null);
        await env.Sstp.DialPause.Started.Task;
        if (cancel) await vm.DisconnectCommand.ExecuteAsync(null);
        else
        {
            await vm.SetSaveCredentialsEnabledAsync(false);
            env.Sstp.DialPause.Release.TrySetResult();
        }
        await connecting;
        Assert.Empty(await env.Store.ListAsync());
        await env.Disconnect.ExecuteAsync();
    });

    [Fact]
    public Task DeletingSavedCredentialAlsoClearsAutofillOnMainScreen() => StaTest.RunAsync(async () =>
    {
        using var env = new MisuseTestEnvironment();
        await env.Store.WriteAsync(new UserConfiguration(true, true));
        await env.Store.SaveAsync("OpenVpn:vpn.example:443", "alice", "secret");
        using var vm = env.CreateViewModel();
        await vm.SetSaveCredentialsEnabledAsync(true);
        await vm.SetSavePasswordAfterConnectionEnabledAsync(true);
        await vm.LoadSavedCredentialsForCurrentServerAsync();
        Assert.Equal("secret", vm.Password);
        await env.Credentials.DeleteAsync("OpenVpn:vpn.example:443");
        await vm.RefreshAfterCredentialsManagementAsync();
        Assert.Equal("", vm.Password);
        Assert.Equal("", vm.Username);
    });

    [Fact]
    public Task Ipv6AddressIsNotSplitAsIfItsLastSegmentWereAPort() => StaTest.RunAsync(async () =>
    {
        using var env = new MisuseTestEnvironment();
        using var vm = env.CreateViewModel();
        vm.SelectedProtocol = VpnProtocol.Sstp;
        vm.ServerHost = "2001:db8::1234";
        vm.ServerPort = "443";
        Assert.Equal("2001:db8::1234", vm.ServerHost);
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal("2001:db8::1234", env.Sstp.LastRequest!.Endpoint.Host);
        Assert.Equal(443, env.Sstp.LastRequest.Endpoint.Port);
        await env.Disconnect.ExecuteAsync();
    });

    [Theory]
    [InlineData(true, true, false, "valid-secret")]
    [InlineData(true, true, true, null)]
    [InlineData(true, false, false, null)]
    [InlineData(false, true, false, null)]
    [InlineData(false, false, false, null)]
    public Task OnlySuccessfulAuthenticationAndBothPreferencesSavePassword(bool saveCredentials, bool savePassword,
        bool rejected, string? expectedPassword) => StaTest.RunAsync(async () =>
    {
        using var env = new MisuseTestEnvironment();
        using var vm = env.CreateViewModel();
        vm.SelectedProtocol = VpnProtocol.Sstp;
        await vm.SetSaveCredentialsEnabledAsync(saveCredentials);
        await vm.SetSavePasswordAfterConnectionEnabledAsync(savePassword);
        vm.Username = "alice";
        vm.SetPasswordFromUser(rejected ? "wrong-secret" : "valid-secret");
        env.Sstp.RejectAuthentication = rejected;
        await vm.ConnectCommand.ExecuteAsync(null);
        var saved = await env.Store.FindAsync("Sstp:vpn.example:443");
        Assert.Equal(expectedPassword, saved?.Password);
        Assert.Equal(saveCredentials && !rejected, saved is not null);
        await env.Disconnect.ExecuteAsync();
    });

    [Fact]
    public Task ExtraKeystrokeOrServerEditDuringDialCannotSaveUnvalidatedValues() => StaTest.RunAsync(async () =>
    {
        using var env = new MisuseTestEnvironment();
        using var vm = env.CreateViewModel();
        vm.SelectedProtocol = VpnProtocol.Sstp;
        await vm.SetSaveCredentialsEnabledAsync(true);
        await vm.SetSavePasswordAfterConnectionEnabledAsync(true);
        vm.Username = "alice";
        vm.SetPasswordFromUser("valid-secret");
        env.Sstp.DialPause = new MisuseTestEnvironment.PausePoint();
        var connecting = vm.ConnectCommand.ExecuteAsync(null);
        await env.Sstp.DialPause.Started.Task;
        Assert.Null(await env.Store.FindAsync("Sstp:vpn.example:443"));
        vm.SetPasswordFromUser("valid-secretX");
        vm.Username = "another-user";
        vm.ServerHost = "other.example";
        env.Sstp.DialPause.Release.TrySetResult();
        await connecting;
        Assert.Equal("valid-secret", env.Sstp.LastRequest!.Password);
        Assert.Equal("alice", (await env.Store.FindAsync("Sstp:vpn.example:443"))?.Username);
        Assert.Equal("valid-secret", (await env.Store.FindAsync("Sstp:vpn.example:443"))?.Password);
        Assert.Null(await env.Store.FindAsync("Sstp:other.example:443"));
        await env.Disconnect.ExecuteAsync();
    });

    [Fact]
    public Task WrongPasswordDoesNotOverwriteSavedPasswordAndRetryUsesCorrectValue() => StaTest.RunAsync(async () =>
    {
        using var env = new MisuseTestEnvironment();
        await env.Store.WriteAsync(new UserConfiguration(true, true));
        await env.Store.SaveAsync("Sstp:vpn.example:443", "alice", "known-good");
        using var vm = env.CreateViewModel();
        vm.SelectedProtocol = VpnProtocol.Sstp;
        await vm.SetSaveCredentialsEnabledAsync(true);
        await vm.SetSavePasswordAfterConnectionEnabledAsync(true);
        await vm.LoadSavedCredentialsForCurrentServerAsync();
        vm.SetPasswordFromUser("known-goodX");
        env.Sstp.RejectAuthentication = true;
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal("known-good", (await env.Store.FindAsync("Sstp:vpn.example:443"))?.Password);
        env.Sstp.RejectAuthentication = false;
        vm.SetPasswordFromUser("known-good");
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal("known-good", env.Sstp.LastRequest!.Password);
        await env.Disconnect.ExecuteAsync();
    });

    [Fact]
    public Task ChangingUserClearsAutofilledPasswordButKeepsManuallyEnteredPassword() => StaTest.RunAsync(async () =>
    {
        using var env = new MisuseTestEnvironment();
        await env.Store.WriteAsync(new UserConfiguration(true, true));
        await env.Store.SaveAsync("OpenVpn:vpn.example:443", "alice", "alice-secret");
        using var vm = env.CreateViewModel();
        await vm.SetSaveCredentialsEnabledAsync(true);
        await vm.SetSavePasswordAfterConnectionEnabledAsync(true);
        await vm.LoadSavedCredentialsForCurrentServerAsync();
        Assert.Equal("alice-secret", vm.Password);
        vm.Username = "bob";
        Assert.Equal("", vm.Password);
        await vm.LoadSavedCredentialsForCurrentServerAsync();
        Assert.Equal("", vm.Password);
        vm.SetPasswordFromUser("bob-secret");
        vm.Username = "charlie";
        Assert.Equal("bob-secret", vm.Password);
    });

    [Fact]
    public Task CancelCredentialChoiceAndInvalidProfileLeavePreviousProfileUsable() => StaTest.RunAsync(async () =>
    {
        using var env = new MisuseTestEnvironment();
        var path = await env.WriteProfileAsync("client\nremote vpn.example 443\nproto tcp-client\nauth-user-pass\n");
        using var vm = env.CreateViewModel(() => path, credentialSource: null);
        await vm.LoadOpenVpnProfileCommand.ExecuteAsync(null);
        var validDescription = vm.OpenVpnProfileSource;
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(0, env.OpenVpn.ConnectCount);
        path = await env.WriteProfileAsync("invalid file\n");
        await vm.LoadOpenVpnProfileCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
        Assert.Equal(validDescription, vm.OpenVpnProfileSource);
        path = null!;
        await vm.LoadOpenVpnProfileCommand.ExecuteAsync(null);
        Assert.Equal(validDescription, vm.OpenVpnProfileSource);
    });

    [Theory]
    [InlineData(OpenVpnCredentialSource.Local, "manual-secret")]
    [InlineData(OpenVpnCredentialSource.Profile, null)]
    public Task FileCredentialChoiceDoesNotPersistUnusedLocalPassword(OpenVpnCredentialSource source, string? expected) => StaTest.RunAsync(async () =>
    {
        using var env = new MisuseTestEnvironment();
        var path = await env.WriteProfileAsync("remote vpn.example 443\n<auth-user-pass>\nfile-user\nfile-secret\n</auth-user-pass>\n");
        using var vm = env.CreateViewModel(() => path, source);
        await vm.SetSaveCredentialsEnabledAsync(true);
        await vm.SetSavePasswordAfterConnectionEnabledAsync(true);
        await vm.LoadOpenVpnProfileCommand.ExecuteAsync(null);
        vm.Username = "alice";
        vm.SetPasswordFromUser("manual-secret");
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(expected, (await env.Store.FindAsync("OpenVpn:vpn.example:443"))?.Password);
        Assert.Equal(source == OpenVpnCredentialSource.Profile, env.OpenVpn.LastRequest!.UseProfileOpenVpnCredentials);
        await env.Disconnect.ExecuteAsync();
    });

    [Fact]
    public Task PublicVpnSelectionAndProtocolSwitchPreserveDefaultCredentialsWithoutMarkingManual() => StaTest.RunAsync(async () =>
    {
        using var env = new MisuseTestEnvironment();
        using var vm = env.CreateViewModel();
        await vm.SetSaveCredentialsEnabledAsync(true);
        await vm.SetSavePasswordAfterConnectionEnabledAsync(true);
        var endpoint = HostEndpoint.Create("public.example", 443);
        var entry = new VpnGateServerEntry("public.example", "192.0.2.1", endpoint, TransportProtocol.Tcp,
            endpoint, "profile", 0, 0, 0, "Japan", "JP", 0, 0, 0, 0, "", "");
        vm.VpnGateList.SelectServerCommand.Execute(new VpnGateServerRowViewModel(entry));
        Assert.Equal("vpn", vm.Username);
        Assert.Equal("vpn", vm.Password);
        vm.SelectedProtocol = VpnProtocol.Sstp;
        await vm.LoadSavedCredentialsForCurrentServerAsync(preserveCurrentCredentials: true);
        Assert.Equal("vpn", vm.Password);
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal("vpn", env.Sstp.LastRequest!.Password);
        Assert.Null((await env.Store.FindAsync("Sstp:public.example:443"))?.Password);
        await env.Disconnect.ExecuteAsync();
        vm.SelectedProtocol = VpnProtocol.OpenVpn;
        Assert.Equal("vpn", vm.Password);
    });

    [Fact]
    public Task RevealAndHideDoNotCountAsTypingAndBothInputsStayInSync() => StaTest.RunAsync(() =>
    {
        var field = new PasswordField();
        var edits = 0;
        field.PasswordChanged += (_, _) => edits++;
        field.Password = "saved-secret";
        var hidden = (PasswordBox)field.FindName("HiddenInput");
        var visible = (TextBox)field.FindName("VisibleInput");
        var reveal = (Button)field.FindName("RevealButton");
        Assert.Equal("saved-secret", hidden.Password);
        reveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(field.IsRevealed);
        Assert.Equal("saved-secret", visible.Text);
        Assert.Equal(0, edits);
        visible.Text = "saved-secretX";
        Assert.Equal("saved-secretX", hidden.Password);
        Assert.Equal("saved-secretX", field.Password);
        Assert.Equal(1, edits);
        field.HidePassword();
        Assert.False(field.IsRevealed);
        hidden.Password = "replaced-by-paste";
        Assert.Equal("replaced-by-paste", visible.Text);
        Assert.Equal(2, edits);
        return Task.CompletedTask;
    });

    [Fact]
    public async Task ManagerListsDeletesAndClearsWithoutChangingPreferences()
    {
        using var env = new MisuseTestEnvironment();
        await env.Store.WriteAsync(new UserConfiguration(false, false, ProcessRoutingBackend.ProxiFyre));
        await env.Store.SaveAsync("OpenVpn:a.example:443", "alice", "alice-secret");
        await env.Store.SaveAsync("Sstp:b.example:443", "bob", "bob-secret");
        var preferences = await File.ReadAllTextAsync(Path.Combine(env.DirectoryPath, "config.json"));
        var listed = await env.Credentials.ListSavedAsync();
        Assert.Equal(2, listed.Count);
        Assert.All(listed, entry => Assert.True(entry.HasPassword));
        Assert.Equal("alice-secret", (await env.Credentials.ReadSavedAsync(listed[0].ServerKey))?.Password);
        await env.Credentials.DeleteAsync(listed[0].ServerKey);
        Assert.Null(await env.Store.FindAsync(listed[0].ServerKey));
        Assert.Single(await env.ReopenStore().ListAsync());
        await env.Credentials.DeleteAsync("missing-key");
        await env.Credentials.ClearAsync();
        Assert.Empty(await env.ReopenStore().ListAsync());
        Assert.Equal(preferences, await File.ReadAllTextAsync(Path.Combine(env.DirectoryPath, "config.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDeleteDoesNotEraseInMemoryCredentials(bool deleteAll)
    {
        using var env = new MisuseTestEnvironment();
        await env.Store.SaveAsync("OpenVpn:a.example:443", "alice", "secret");
        using (var locked = File.Open(Path.Combine(env.DirectoryPath, "user_auth.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Record.ExceptionAsync(() => deleteAll ? env.Credentials.ClearAsync() : env.Credentials.DeleteAsync("OpenVpn:a.example:443"));
            Assert.True(error is IOException or UnauthorizedAccessException, $"Falha de arquivo esperada; recebido: {error?.GetType().Name ?? "nenhuma"}.");
        }
        Assert.Equal("secret", (await env.Store.FindAsync("OpenVpn:a.example:443"))?.Password);
        Assert.Single(await env.ReopenStore().ListAsync());
    }
}
