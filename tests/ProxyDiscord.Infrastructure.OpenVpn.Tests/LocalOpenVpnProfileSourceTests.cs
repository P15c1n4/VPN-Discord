using System.Text;
using ProxyDiscord.Domain.ValueObjects;
using ProxyDiscord.Infrastructure.OpenVpn;

namespace ProxyDiscord.Infrastructure.OpenVpn.Tests;

public class LocalOpenVpnProfileSourceTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pd-ovpn-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly LocalOpenVpnProfileSource _source = new();

    private string WriteProfile(string content, string name = "perfil.ovpn")
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task LoadAsync_InlineProfile_ReturnsEndpointAndBase64()
    {
        var content = "client\ndev tun\nproto udp\nremote vpn.example.com 1195\n<ca>\nMIIB\n</ca>\n";
        var path = WriteProfile(content);

        var profile = await _source.LoadAsync(path);

        Assert.Equal("perfil.ovpn", profile.FileName);
        Assert.Equal("vpn.example.com", profile.Endpoint.Host);
        Assert.Equal(1195, profile.Endpoint.Port);
        Assert.Equal(TransportProtocol.Udp, profile.Transport);
        Assert.Equal(content, Encoding.UTF8.GetString(Convert.FromBase64String(profile.ConfigBase64)));
    }

    [Fact]
    public async Task LoadAsync_NoProtoDirective_DefaultsToTcp()
    {
        var path = WriteProfile("client\nremote 1.2.3.4 443\n<ca>\nx\n</ca>\n");

        var profile = await _source.LoadAsync(path);

        Assert.Equal(TransportProtocol.Tcp, profile.Transport);
    }

    [Fact]
    public async Task LoadAsync_CommentedRemoteOnly_IsRejected()
    {
        var path = WriteProfile("client\ndev tun\n#remote 9.9.9.9 1111\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _source.LoadAsync(path));

        Assert.Contains("remote", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ca ca.crt")]
    [InlineData("cert client.crt")]
    [InlineData("key client.key")]
    [InlineData("tls-auth ta.key 1")]
    public async Task LoadAsync_ProfileReferencingAnExternalFile_IsRejected(string directive)
    {
        var path = WriteProfile($"client\nremote 1.2.3.4 443\n{directive}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _source.LoadAsync(path));

        Assert.Contains("arquivo externo", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_InlineBlockPresent_IsNotMistakenForAnExternalReference()
    {
        var path = WriteProfile("client\nremote 1.2.3.4 443\n<ca>\nMIIB\n</ca>\n<tls-auth>\nkey\n</tls-auth>\n");

        var profile = await _source.LoadAsync(path);

        Assert.Equal("1.2.3.4", profile.Endpoint.Host);
    }

    [Fact]
    public async Task LoadAsync_CommentedDirective_IsNotMistakenForAnExternalReference()
    {
        var path = WriteProfile("client\nremote 1.2.3.4 443\n# ca ca.crt\n;cert client.crt\n");

        var profile = await _source.LoadAsync(path);

        Assert.Equal(443, profile.Endpoint.Port);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_IsRejected()
    {
        var missing = Path.Combine(_directory, "nao-existe.ovpn");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _source.LoadAsync(missing));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
