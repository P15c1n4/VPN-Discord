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
            throw new InvalidOperationException($"Arquivo não encontrado: {filePath}");
        }

        if (file.Length > MAX_PROFILE_BYTES)
        {
            throw new InvalidOperationException(
                $"O arquivo tem {file.Length / 1024} KB e não parece um perfil OpenVPN (limite: {MAX_PROFILE_BYTES / 1024} KB).");
        }

        var config = await File.ReadAllTextAsync(filePath, cancellationToken);

        if (OpenVpnRemoteParser.TryParse(config) is not { } remote)
        {
            if (FindBareIpEndpoint(config) is { } bareEndpoint)
            {
                throw new InvalidOperationException(
                    $"A linha '{bareEndpoint}' está sem a diretiva 'remote'. " +
                    $"Use 'remote {bareEndpoint}' no perfil OpenVPN.");
            }

            throw new InvalidOperationException(
                "O arquivo não contém uma diretiva 'remote' válida; não é um perfil OpenVPN de cliente.");
        }

        if (FindExternalFileReference(config) is { } directive)
        {
            throw new InvalidOperationException(
                $"O perfil aponta para um arquivo externo ('{directive}'). Use um .ovpn com os certificados " +
                $"embutidos (blocos <{directive}>...</{directive}>).");
        }

        return new OpenVpnProfileDescriptor(
            file.Name,
            file.FullName,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(config)),
            HostEndpoint.Create(remote.Host, remote.Port),
            remote.Transport);
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
