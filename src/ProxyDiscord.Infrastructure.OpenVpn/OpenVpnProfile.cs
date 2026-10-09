using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ProxyDiscord.Infrastructure.OpenVpn;

internal sealed record OpenVpnProfile(
    string Directory,
    string ConfigPath,
    string UpScriptPath,
    string TunnelInfoPath,
    string ManagementPasswordFilePath,
    string ManagementPassword) : IDisposable
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
    private static readonly string DEFAULT_ROOT_DIRECTORY = Path.Combine(AppContext.BaseDirectory, "openvpn", "sessions");

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

        var profile = new OpenVpnProfile(
            sessionDirectory,
            Path.Combine(sessionDirectory, "session.ovpn"),
            Path.Combine(sessionDirectory, "up.bat"),
            Path.Combine(sessionDirectory, "tunnel.json"),
            Path.Combine(sessionDirectory, "management.pw"),
            CreateManagementPassword());

        try
        {
            // A senha de gerenciamento só pode ser gravada depois que a pasta estiver protegida.
            RestrictToAdministrators(sessionDirectory);
            File.WriteAllText(profile.ManagementPasswordFilePath, profile.ManagementPassword, Encoding.ASCII);
            File.WriteAllText(profile.UpScriptPath, BuildUpScript(profile.TunnelInfoPath), Encoding.ASCII);
            File.WriteAllText(
                profile.ConfigPath,
                BuildConfig(
                    published, profile, adapterName, managementPort, hasCredentials, useProfileCredentials),
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
        var normalizedPublished = RemoveAppManagedDirectives(
            published, preserveProfileAuthentication: useProfileCredentials);
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
            builder.AppendLine("# Credenciais respondidas pela interface de gerenciamento e mantidas em memória.");
            builder.AppendLine("auth-user-pass");
        }

        // O gerenciamento também atende pedidos de senha de chave privada, mesmo quando
        // o perfil não usa autenticação por usuário/senha.
        builder.AppendLine("management-query-passwords");
        builder.AppendLine();

        builder.AppendLine("# Adaptador criado por este app, para não disputar adaptador com outro cliente.");
        builder.AppendLine("windows-driver tap-windows6");
        builder.AppendLine($"dev-node {Quote(adapterName)}");
        builder.AppendLine();

        builder.AppendLine("# O endereço e o gateway do túnel só existem depois que a interface sobe; o script");
        builder.AppendLine("# up é a forma documentada de capturá-los, e é deles que sai a rota do túnel.");
        builder.AppendLine("script-security 2");
        builder.AppendLine($"up {Quote(profile.UpScriptPath)}");
        builder.AppendLine("up-restart");
        builder.AppendLine();

        builder.AppendLine("# Interface de gerenciamento: é daqui que vem o estado real da conexão, em vez de");
        builder.AppendLine("# adivinhar pelo log ou pelo tempo decorrido.");
        builder.AppendLine(
            $"management 127.0.0.1 {managementPort} {Quote(profile.ManagementPasswordFilePath)}");
        builder.AppendLine("management-hold");
        builder.AppendLine();

        builder.AppendLine("verb 3");
        return builder.ToString();
    }

    private static readonly HashSet<string> APP_MANAGED_DIRECTIVES = new(StringComparer.OrdinalIgnoreCase)
    {
        "askpass",
        "auth-token",
        "auth-user-pass",
        "auth-user-pass-verify",
        "block-outside-dns",
        "cd",
        "client-connect",
        "client-disconnect",
        "config",
        "connect-retry",
        "connect-retry-max",
        "connect-timeout",
        "daemon",
        "down",
        "down-pre",
        "dhcp-option",
        "ipchange",
        "learn-address",
        "log",
        "log-append",
        "management",
        "management-client",
        "management-external-cert",
        "management-external-key",
        "management-hold",
        "management-log-cache",
        "management-query-passwords",
        "management-query-proxy",
        "management-query-remote",
        "management-signal",
        "management-up-down",
        "plugin",
        "plugin-dir",
        "pull-filter",
        "redirect-gateway",
        "redirect-private",
        "route",
        "route-ipv6",
        "route-nopull",
        "route-pre-down",
        "route-up",
        "resolv-retry",
        "script-security",
        "server-poll-timeout",
        "status",
        "status-version",
        "tls-crypt-v2-verify",
        "tls-export-cert",
        "tls-verify",
        "up",
        "up-restart",
        "writepid",
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
            if (!IsAppManagedDirective(directive) ||
                (preserveProfileAuthentication &&
                 string.Equals(directive, "auth-user-pass", StringComparison.OrdinalIgnoreCase)))
            {
                lines.Add(raw);
            }
        }

        return string.Join("\n", lines);
    }

    private static bool IsAppManagedDirective(string directive) =>
        APP_MANAGED_DIRECTIVES.Contains(directive) ||
        directive.StartsWith("management-", StringComparison.OrdinalIgnoreCase);

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

    private static string CreateManagementPassword()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        try
        {
            return Convert.ToHexString(bytes);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string BuildUpScript(string tunnelInfoPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("@echo off");
        builder.AppendLine(
            $"> \"{tunnelInfoPath}\" echo {{\"dev\":\"%dev%\",\"ifconfig_local\":\"%ifconfig_local%\",\"ifconfig_remote\":\"%ifconfig_remote%\",\"ifconfig_netmask\":\"%ifconfig_netmask%\",\"route_vpn_gateway\":\"%route_vpn_gateway%\"}}");
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
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or
                                   System.Security.SecurityException or IOException)
        {
            throw new InvalidOperationException(
                "Não foi possível proteger os arquivos temporários da sessão OpenVPN; a conexão foi cancelada.", ex);
        }
    }

    // O conteúdo da sessão pode conter detalhes do servidor e arquivos de execução temporários.
    // Incluímos a identidade atual para que o processo consiga remover a própria pasta ao encerrar.
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
