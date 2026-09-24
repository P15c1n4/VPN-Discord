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
        string configBase64,
        string? username,
        string? password,
        string adapterName,
        int managementPort,
        bool useProfileCredentials = false)
    {
        var published = Decode(configBase64);
        var hasUsername = !useProfileCredentials && !string.IsNullOrWhiteSpace(username);
        var hasPassword = !useProfileCredentials && !string.IsNullOrWhiteSpace(password);

        if (hasUsername != hasPassword)
        {
            throw new InvalidOperationException(
                "Para usar login no OpenVPN, preencha usuário e senha ou deixe ambos em branco.");
        }

        var hasCredentials = hasUsername && hasPassword;

        if (!hasCredentials && !HasClientAuthenticationMethod(published, useProfileCredentials))
        {
            throw new InvalidOperationException(
                "O perfil OpenVPN não contém certificado/chave do cliente nem usuário e senha. " +
                "Adicione a autenticação ao .ovpn ou informe as credenciais do servidor.");
        }

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
            if (hasCredentials)
            {
                File.WriteAllText(profile.CredentialsPath, $"{username}\n{password}\n", new UTF8Encoding(false));
            }

            File.WriteAllText(profile.UpScriptPath, BuildUpScript(profile.TunnelInfoPath), Encoding.ASCII);
            File.WriteAllText(
                profile.ConfigPath,
                BuildConfig(published, profile, adapterName, managementPort, hasCredentials, useProfileCredentials),
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
                "O perfil OpenVPN não contém uma diretiva 'remote'. Selecione um perfil válido para continuar.");
        }

        return decoded;
    }

    private static string BuildConfig(
        string published,
        OpenVpnProfile profile,
        string adapterName,
        int managementPort,
        bool hasCredentials,
        bool useProfileCredentials)
    {
        var normalizedPublished = RemoveAppManagedDirectives(published, preserveProfileAuthentication: useProfileCredentials);
        var builder = new StringBuilder();
        builder.AppendLine("# Gerado por ProxyDiscord. Base: perfil publicado pelo servidor VPN Gate.");
        builder.AppendLine(normalizedPublished.TrimEnd());
        builder.AppendLine();
        builder.AppendLine("# --- Ajustes do ProxyDiscord -------------------------------------------------");
        builder.AppendLine();

        builder.AppendLine("# O tráfego de UM processo é roteado pelo túnel; o resto da máquina não pode ser");
        builder.AppendLine("# afetado. Os filtros aceitam route-gateway/topology do servidor, necessários para");
        builder.AppendLine("# descobrir o próximo salto, mas bloqueiam rotas e DNS globais. A rota controlada");
        builder.AppendLine("# do túnel é instalada pelo app, com métrica alta.");
        builder.AppendLine("pull-filter ignore \"redirect-gateway\"");
        builder.AppendLine("pull-filter ignore \"redirect-private\"");
        builder.AppendLine("pull-filter ignore \"route \"");
        builder.AppendLine("pull-filter ignore \"route-ipv6 \"");
        builder.AppendLine("pull-filter ignore \"block-outside-dns\"");
        builder.AppendLine("pull-filter ignore \"dhcp-option DNS\"");
        builder.AppendLine();

        if (hasCredentials)
        {
            builder.AppendLine("# Credenciais em arquivo: não há console para o OpenVPN pedir usuário e senha.");
            builder.AppendLine($"auth-user-pass {Quote(profile.CredentialsPath)}");
            builder.AppendLine();
        }

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

    private static readonly HashSet<string> APP_MANAGED_DIRECTIVES = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth-user-pass",
        "block-outside-dns",
        "dhcp-option",
        "pull-filter",
        "redirect-gateway",
        "redirect-private",
        "route",
        "route-ipv6",
        "route-nopull",
    };

    private static string RemoveAppManagedDirectives(string config, bool preserveProfileAuthentication)
    {
        var lines = new List<string>();
        var insideAuthBlock = false;
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (string.Equals(line, "<auth-user-pass>", StringComparison.OrdinalIgnoreCase))
            {
                insideAuthBlock = true;
                if (preserveProfileAuthentication)
                {
                    lines.Add(raw);
                }

                continue;
            }

            if (insideAuthBlock)
            {
                if (string.Equals(line, "</auth-user-pass>", StringComparison.OrdinalIgnoreCase))
                {
                    insideAuthBlock = false;
                    if (preserveProfileAuthentication)
                    {
                        lines.Add(raw);
                    }
                }
                else if (preserveProfileAuthentication)
                {
                    lines.Add(raw);
                }

                continue;
            }

            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                lines.Add(raw);
                continue;
            }

            var separator = line.IndexOfAny([' ', '\t']);
            var directive = separator < 0 ? line : line[..separator];
            if (!APP_MANAGED_DIRECTIVES.Contains(directive) ||
                (preserveProfileAuthentication && string.Equals(directive, "auth-user-pass", StringComparison.OrdinalIgnoreCase)))
            {
                lines.Add(raw);
            }
        }

        return string.Join("\n", lines);
    }

    private static bool HasClientAuthenticationMethod(string config, bool includeUsernamePassword)
    {
        if (includeUsernamePassword &&
            (config.Contains("<auth-user-pass>", StringComparison.OrdinalIgnoreCase) ||
             HasDirectiveWithArgument(config, "auth-user-pass") ||
             config.Split('\n').Any(line => string.Equals(line.Trim(), "auth-user-pass", StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        if (HasInlineBlock(config, "cert") && HasInlineBlock(config, "key") ||
            HasInlineBlock(config, "pkcs12"))
        {
            return true;
        }

        // Profiles obtained from VPN providers sometimes reference certificate files
        // instead of embedding them. These are accepted here so OpenVPN can report a
        // precise missing-file error; local profile loading already rejects unsupported
        // external certificate references before this point.
        return HasDirectiveWithArgument(config, "cert") && HasDirectiveWithArgument(config, "key") ||
               HasDirectiveWithArgument(config, "pkcs12") ||
               HasDirectiveWithArgument(config, "secret");
    }

    private static bool HasInlineBlock(string config, string directive) =>
        config.Contains($"<{directive}>", StringComparison.OrdinalIgnoreCase) &&
        config.Contains($"</{directive}>", StringComparison.OrdinalIgnoreCase);

    private static bool HasDirectiveWithArgument(string config, string directive)
    {
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith(directive + " ", StringComparison.OrdinalIgnoreCase) &&
                line.Length > directive.Length + 1)
            {
                return true;
            }
        }

        return false;
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
