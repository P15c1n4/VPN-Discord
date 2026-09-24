using System.Text;
using System.Net;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Application.Vpn;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.OpenVpn;

public sealed class LocalOpenVpnProfileSource : IOpenVpnProfileSource
{
    private const long MAX_PROFILE_BYTES = 4 * 1024 * 1024;

    private static readonly string[] EXTERNAL_FILE_DIRECTIVES = ["ca", "cert", "key", "pkcs12", "tls-auth", "tls-crypt"];

    public async Task<OpenVpnProfileDescriptor> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists)
        {
            throw new InvalidOperationException($"O arquivo não foi encontrado: {filePath}");
        }

        if (file.Length > MAX_PROFILE_BYTES)
        {
            throw new InvalidOperationException(
                $"O arquivo tem {file.Length / 1024} KB e excede o limite de {MAX_PROFILE_BYTES / 1024} KB para um perfil OpenVPN.");
        }

        var config = await File.ReadAllTextAsync(filePath, cancellationToken);

        if (OpenVpnRemoteParser.TryParse(config) is not { } remote)
        {
            if (FindBareIpEndpoint(config) is { } bareEndpoint)
            {
                throw new InvalidOperationException(
                    $"O endereço '{bareEndpoint}' não tem a diretiva 'remote'. " +
                    $"Inclua 'remote {bareEndpoint}' no perfil OpenVPN.");
            }

            throw new InvalidOperationException(
                "O arquivo não contém uma diretiva 'remote' válida e não pode ser usado como perfil OpenVPN de cliente.");
        }

        if (FindExternalFileReference(config) is { } directive)
        {
            throw new InvalidOperationException(
                $"O perfil referencia um arquivo externo ('{directive}'). Para usar esse perfil no aplicativo, " +
                $"inclua o conteúdo em um bloco <{directive}>...</{directive}> dentro do .ovpn.");
        }

        var (profileConfig, authentication) = InspectAuthentication(config, file.DirectoryName!);

        return new OpenVpnProfileDescriptor(
            file.Name,
            file.FullName,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(profileConfig)),
            HostEndpoint.Create(remote.Host, remote.Port),
            remote.Transport,
            authentication);
    }

    private static (string Config, OpenVpnAuthenticationInfo Authentication) InspectAuthentication(
        string config, string profileDirectory)
    {
        var lines = config.Split('\n');
        var inlineStart = Array.FindIndex(lines, line =>
            string.Equals(line.Trim(), "<auth-user-pass>", StringComparison.OrdinalIgnoreCase));
        if (inlineStart >= 0)
        {
            var inlineEnd = Array.FindIndex(lines, inlineStart + 1, line =>
                string.Equals(line.Trim(), "</auth-user-pass>", StringComparison.OrdinalIgnoreCase));
            if (inlineEnd <= inlineStart)
            {
                throw new InvalidOperationException(
                    "O bloco <auth-user-pass> do perfil não foi encerrado corretamente.");
            }

            var values = lines[(inlineStart + 1)..inlineEnd]
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToArray();
            var hasUsernameAndPassword = values.Length >= 2 &&
                                         !string.IsNullOrWhiteSpace(values[0]) &&
                                         !string.IsNullOrWhiteSpace(values[1]);
            return (config, new OpenVpnAuthenticationInfo(
                OpenVpnAuthenticationKind.InlineCredentials, hasUsernameAndPassword));
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';') ||
                !line.StartsWith("auth-user-pass", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.Length > "auth-user-pass".Length &&
                !char.IsWhiteSpace(line["auth-user-pass".Length]))
            {
                continue;
            }

            var argument = line["auth-user-pass".Length..].Trim();
            if (argument.Length == 0)
            {
                return (config, new OpenVpnAuthenticationInfo(
                    OpenVpnAuthenticationKind.InteractivePrompt, ProfileOptionAvailable: false));
            }

            if (!TryGetFilePath(argument, out var referencedPath))
            {
                return (config, new OpenVpnAuthenticationInfo(
                    OpenVpnAuthenticationKind.ExternalCredentialsFile, ProfileOptionAvailable: false));
            }

            string absolutePath;
            try
            {
                absolutePath = Path.GetFullPath(Path.IsPathRooted(referencedPath)
                    ? referencedPath
                    : Path.Combine(profileDirectory, referencedPath));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return (config, new OpenVpnAuthenticationInfo(
                    OpenVpnAuthenticationKind.ExternalCredentialsFile, ProfileOptionAvailable: false));
            }
            var usable = false;
            try
            {
                var credentialLines = File.ReadAllLines(absolutePath);
                usable = credentialLines.Length >= 2 &&
                         !string.IsNullOrWhiteSpace(credentialLines[0]) &&
                         !string.IsNullOrWhiteSpace(credentialLines[1]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }

            lines[i] = $"auth-user-pass \"{absolutePath.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
            return (string.Join('\n', lines), new OpenVpnAuthenticationInfo(
                OpenVpnAuthenticationKind.ExternalCredentialsFile, usable));
        }

        return (config, new OpenVpnAuthenticationInfo(
            OpenVpnAuthenticationKind.NoUsernamePasswordDirective,
            ProfileOptionAvailable: HasClientAuthenticationMethod(config)));
    }

    private static bool HasClientAuthenticationMethod(string config)
    {
        if (HasInlineBlock(config, "cert") && HasInlineBlock(config, "key") ||
            HasInlineBlock(config, "pkcs12"))
        {
            return true;
        }

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

    private static bool TryGetFilePath(string argument, out string path)
    {
        path = argument;
        if (argument.StartsWith('"'))
        {
            if (argument.Length < 2 || !argument.EndsWith('"'))
            {
                return false;
            }

            path = argument[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        else if (argument.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(path);
    }

    private static string? FindExternalFileReference(string config)
    {
        var lines = config.Split('\n');

        foreach (var directive in EXTERNAL_FILE_DIRECTIVES)
        {
            if (config.Contains($"<{directive}>", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith('#') || line.StartsWith(';'))
                {
                    continue;
                }

                if (line.StartsWith(directive + " ", StringComparison.OrdinalIgnoreCase) &&
                    line.Length > directive.Length + 1)
                {
                    return directive;
                }
            }
        }

        return null;
    }

    private static string? FindBareIpEndpoint(string config)
    {
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is 2 or 3 &&
                IPAddress.TryParse(parts[0], out _) &&
                int.TryParse(parts[1], out var port) &&
                port is > 0 and <= 65535)
            {
                return line;
            }
        }

        return null;
    }
}
