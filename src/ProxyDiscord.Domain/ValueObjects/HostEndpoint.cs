using ProxyDiscord.Domain.Exceptions;

namespace ProxyDiscord.Domain.ValueObjects;

public sealed record HostEndpoint
{
    public string Host { get; }
    public int Port { get; }

    private HostEndpoint(string host, int port)
    {
        Host = host;
        Port = port;
    }

    public static HostEndpoint Create(string? host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new AddressParseException("O endereço do servidor não pode ser vazio.");
        }

        if (port is <= 0 or > 65535)
        {
            throw new AddressParseException($"Porta inválida: {port} não é um número entre 1 e 65535.");
        }

        return new HostEndpoint(host.Trim(), port);
    }

    public static HostEndpoint Parse(string? raw, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new AddressParseException("O endereço do servidor não pode ser vazio.");
        }

        var trimmed = raw.Trim();
        var lastColon = trimmed.LastIndexOf(':');

        if (lastColon > 0 && lastColon < trimmed.Length - 1)
        {
            var hostPart = trimmed[..lastColon];
            var portPart = trimmed[(lastColon + 1)..];

            if (int.TryParse(portPart, out var parsedPort) && parsedPort is > 0 and <= 65535)
            {
                return new HostEndpoint(hostPart, parsedPort);
            }

            if (!hostPart.Contains(':'))
            {
                throw new AddressParseException(
                    $"Porta inválida em '{raw}': '{portPart}' não é um número entre 1 e 65535.");
            }
        }

        return new HostEndpoint(trimmed, defaultPort);
    }

    public override string ToString() => $"{Host}:{Port}";
}
