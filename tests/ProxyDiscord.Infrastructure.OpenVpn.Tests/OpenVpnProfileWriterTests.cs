using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDiscord.Infrastructure.OpenVpn;

namespace ProxyDiscord.Infrastructure.OpenVpn.Tests;

public class OpenVpnProfileWriterTests : IDisposable
{
    private const string SERVER_PROFILE = "client\ndev tun\nproto tcp\nremote 219.100.37.109 443\n";

    // Raiz própria por execução: o writer endurece o ACL do diretório de sessão, e apontar isso
    // para o %ProgramData% real deixaria resíduo com credenciais na máquina de quem roda a suíte.
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ProxyDiscord.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private OpenVpnProfileWriter CreateWriter() =>
        new(NullLogger<OpenVpnProfileWriter>.Instance, _root);

    private void WithProfile(Action<string, OpenVpnProfile> assert, string profileText = SERVER_PROFILE)
    {
        var profile = CreateWriter().Write(Base64(profileText), "vpn", "vpn", "Discord-VPN Tunnel", 31990);
        try
        {
            assert(File.ReadAllText(profile.ConfigPath), profile);
        }
        finally
        {
            profile.Dispose();
        }
    }

    [Fact]
    public void Write_EscapesBackslashesInEveryQuotedPath()
    {
        WithProfile((config, profile) =>
        {
            foreach (var directive in new[] { "auth-user-pass", "up", "log" })
            {
                var line = config.Split('\n').Single(l => l.StartsWith(directive + " ", StringComparison.Ordinal));
                var quoted = line[(line.IndexOf('"') + 1)..line.LastIndexOf('"')];

                Assert.False(HasLoneBackslash(quoted), $"'{directive}' escreveu um caminho não escapado: {quoted}");
            }

            Assert.Contains(profile.CredentialsPath.Replace(@"\", @"\\"), config, StringComparison.Ordinal);
            Assert.Contains(profile.UpScriptPath.Replace(@"\", @"\\"), config, StringComparison.Ordinal);
            Assert.Contains(profile.LogPath.Replace(@"\", @"\\"), config, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Write_KeepsThePublishedProfileAndAddsTheSplitTunnellingOverrides()
    {
        WithProfile((config, _) =>
        {
            Assert.Contains("remote 219.100.37.109 443", config, StringComparison.Ordinal);

            Assert.Contains("\nroute-nopull", config, StringComparison.Ordinal);
            Assert.Contains("management 127.0.0.1 31990", config, StringComparison.Ordinal);
            Assert.Contains("management-hold", config, StringComparison.Ordinal);
            Assert.Contains("dev-node \"Discord-VPN Tunnel\"", config, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Write_StoresTheCredentialsWhereTheConfigPointsTo()
    {
        WithProfile((_, profile) =>
        {
            Assert.Equal("vpn\nvpn\n", File.ReadAllText(profile.CredentialsPath));
        });
    }

    [Fact]
    public void Write_ProfileWithoutRemote_IsRejectedWithoutLeavingASessionDirectory()
    {
        var before = ExistingSessionDirectories();

        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateWriter().Write(Base64("client\ndev tun\n"), "vpn", "vpn", "Discord-VPN Tunnel", 31990));

        Assert.Contains("remote", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, ExistingSessionDirectories());
    }

    private HashSet<string> ExistingSessionDirectories() =>
        Directory.Exists(_root) ? [.. Directory.GetDirectories(_root)] : [];

    [Fact]
    public void Dispose_RemovesTheSessionDirectory()
    {
        var profile = CreateWriter().Write(Base64(SERVER_PROFILE), "vpn", "vpn", "Discord-VPN Tunnel", 31990);
        Assert.True(Directory.Exists(profile.Directory));

        profile.Dispose();

        Assert.False(Directory.Exists(profile.Directory));
    }

    // O auth.txt fica em texto puro no disco: o diretório de sessão não pode herdar as permissões
    // do pai, e quem escreveu precisa continuar podendo escrever e apagar (foi essa segunda metade
    // que faltava, e trancava o writer para fora do próprio diretório).
    [Fact]
    public void Write_RestrictsTheSessionDirectoryWithoutLockingTheWriterOut()
    {
        WithProfile((_, profile) =>
        {
            var security = new DirectoryInfo(profile.Directory).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);

            var granted = security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Where(rule => rule.AccessControlType == AccessControlType.Allow)
                .Select(rule => (SecurityIdentifier)rule.IdentityReference)
                .ToList();

            Assert.Contains(granted, sid => sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
            Assert.Contains(granted, sid => sid.IsWellKnown(WellKnownSidType.LocalSystemSid));
            Assert.Contains(granted, sid => sid == WindowsIdentity.GetCurrent().User);

            File.WriteAllText(profile.TunnelInfoPath, "dev=tap0\n");
            Assert.Equal("dev=tap0\n", File.ReadAllText(profile.TunnelInfoPath));
        });
    }

    private static bool HasLoneBackslash(string quoted)
    {
        for (var i = 0; i < quoted.Length; i++)
        {
            if (quoted[i] != '\\')
            {
                continue;
            }

            if (i + 1 >= quoted.Length || quoted[i + 1] != '\\')
            {
                return true;
            }

            i++;
        }

        return false;
    }
}
