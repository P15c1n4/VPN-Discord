using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ProxyDiscord.Infrastructure.OpenVpn;

internal sealed record OpenVpnProfile(
    string Directory,
    string ConfigPath,
    string CredentialsPath,
    string UpScriptPath,
    string TunnelInfoPath,
    string LogPath) : IDisposable
{
    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class OpenVpnProfileWriter(ILogger<OpenVpnProfileWriter> logger, string? rootDirectory = null)
{
    private static readonly string DEFAULT_ROOT_DIRECTORY = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProxyDiscord", "openvpn");

    private readonly string _rootDirectory = rootDirectory ?? DEFAULT_ROOT_DIRECTORY;

    public OpenVpnProfile Write(
        string configBase64, string username, string password, string adapterName, int managementPort)
    {
        var published = Decode(configBase64);

        var sessionDirectory = Path.Combine(_rootDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDirectory);
        RestrictToAdministrators(sessionDirectory);

        var profile = new OpenVpnProfile(
            sessionDirectory,
            Path.Combine(sessionDirectory, "session.ovpn"),
            Path.Combine(sessionDirectory, "auth.txt"),
            Path.Combine(sessionDirectory, "up.bat"),
            Path.Combine(sessionDirectory, "tunnel.txt"),
            Path.Combine(sessionDirectory, "openvpn.log"));

        try
        {
            File.WriteAllText(profile.CredentialsPath, $"{username}\n{password}\n", new UTF8Encoding(false));
            File.WriteAllText(profile.UpScriptPath, BuildUpScript(profile.TunnelInfoPath), Encoding.ASCII);
            File.WriteAllText(
                profile.ConfigPath,
                BuildConfig(published, profile, adapterName, managementPort),
                new UTF8Encoding(false));
        }
        catch
        {
            profile.Dispose();
            throw;
        }

        logger.LogDebug("Perfil OpenVPN gerado em {Directory}", sessionDirectory);
        return profile;
    }

    private static string Decode(string configBase64)
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(configBase64.Trim()));
        if (!decoded.Contains("remote ", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "O perfil OpenVPN publicado pelo servidor não contém nenhuma diretiva 'remote'.");
        }

        return decoded;
    }

    private static string BuildConfig(
        string published, OpenVpnProfile profile, string adapterName, int managementPort)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Gerado por ProxyDiscord. Base: perfil publicado pelo servidor VPN Gate.");
        builder.AppendLine(published.TrimEnd());
        builder.AppendLine();
        builder.AppendLine("# --- Ajustes do ProxyDiscord -------------------------------------------------");
        builder.AppendLine();

        builder.AppendLine("# O tráfego de UM processo é roteado pelo túnel; o resto da máquina não pode ser");
        builder.AppendLine("# afetado. Sem route-nopull o servidor empurra redirect-gateway e sequestra a rota");
        builder.AppendLine("# padrão. A rota do túnel é instalada pelo app, com métrica alta.");
        builder.AppendLine("route-nopull");
        builder.AppendLine();

        builder.AppendLine("# Credenciais em arquivo: não há console para o OpenVPN pedir usuário e senha.");
        builder.AppendLine($"auth-user-pass {Quote(profile.CredentialsPath)}");
        builder.AppendLine();

        builder.AppendLine("# Adaptador criado por este app, para não disputar adaptador com outro cliente.");
        builder.AppendLine("windows-driver tap-windows6");
        builder.AppendLine($"dev-node {Quote(adapterName)}");
        builder.AppendLine();

        builder.AppendLine("# O endereço e o gateway do túnel só existem depois que a interface sobe; o script");
        builder.AppendLine("# up é a forma documentada de capturá-los, e é deles que sai a rota do túnel.");
        builder.AppendLine("script-security 2");
        builder.AppendLine($"up {Quote(profile.UpScriptPath)}");
        builder.AppendLine();

        builder.AppendLine("# Interface de gerenciamento: é daqui que vem o estado real da conexão, em vez de");
        builder.AppendLine("# adivinhar pelo log ou pelo tempo decorrido.");
        builder.AppendLine($"management 127.0.0.1 {managementPort}");
        builder.AppendLine("management-hold");
        builder.AppendLine();

        builder.AppendLine($"log {Quote(profile.LogPath)}");
        builder.AppendLine("verb 3");
        builder.AppendLine("connect-retry-max 2");
        builder.AppendLine("resolv-retry 20");
        return builder.ToString();
    }

    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\")}\"";

    private static string BuildUpScript(string tunnelInfoPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("@echo off");
        builder.AppendLine($"> \"{tunnelInfoPath}\" echo dev=%dev%");
        builder.AppendLine($">> \"{tunnelInfoPath}\" echo ifconfig_local=%ifconfig_local%");
        builder.AppendLine($">> \"{tunnelInfoPath}\" echo ifconfig_remote=%ifconfig_remote%");
        builder.AppendLine($">> \"{tunnelInfoPath}\" echo ifconfig_netmask=%ifconfig_netmask%");
        builder.AppendLine($">> \"{tunnelInfoPath}\" echo route_vpn_gateway=%route_vpn_gateway%");
        builder.AppendLine("exit /b 0");
        return builder.ToString();
    }

    private void RestrictToAdministrators(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (var identity in GrantedIdentities())
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    identity,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            info.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            logger.LogWarning(ex, "Não foi possível restringir as permissões de {Directory}", directory);
        }
    }

    // Administradores e SYSTEM porque o auth.txt guarda usuário e senha em texto puro. A identidade
    // do próprio processo entra junto: sem ela o writer se tranca para fora dos arquivos que acabou
    // de criar — a herança é removida no mesmo passo — e nem o Dispose consegue apagar o diretório,
    // que fica vazando credenciais. Em produção o app roda elevado e esse SID já está coberto por
    // Administradores; num processo não elevado é o que mantém o writer utilizável.
    private static IEnumerable<IdentityReference> GrantedIdentities()
    {
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        var current = WindowsIdentity.GetCurrent().User;
        if (current is not null)
        {
            yield return current;
        }
    }
}
