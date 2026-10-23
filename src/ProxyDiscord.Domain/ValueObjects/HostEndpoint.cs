using ProxyDiscord.Domain.Exceptions;
using System.Net;

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
            throw new AddressParseException("Informe o endereço do servidor.");
        }

        if (port is <= 0 or > 65535)
        {
            throw new AddressParseException($"A porta {port} é inválida. Informe um número entre 1 e 65535.");
        }

        var normalized = host.Trim();
        if (normalized.StartsWith('[') && normalized.EndsWith(']')) normalized = normalized[1..^1];
        var numericIpv4 = normalized.Contains('.') && normalized.All(character => char.IsAsciiDigit(character) || character == '.');
        if (Uri.CheckHostName(normalized) == UriHostNameType.Unknown ||
            numericIpv4 && (normalized.Split('.').Length != 4 || !IPAddress.TryParse(normalized, out _)))
        {
            throw new AddressParseException("O endereço do servidor é inválido. Informe um host ou IP, sem URL ou espaços.");
        }
        return new HostEndpoint(normalized, port);
    }

    public static HostEndpoint Parse(string? raw, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new AddressParseException("Informe o endereço do servidor.");
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            var closing = trimmed.IndexOf(']');
            if (closing < 0) throw new AddressParseException("O endereço IPv6 está incompleto.");
            var suffix = trimmed[(closing + 1)..];
            if (suffix.Length == 0) return Create(trimmed[1..closing], defaultPort);
            if (!suffix.StartsWith(':') || !int.TryParse(suffix[1..], out var bracketPort))
                throw new AddressParseException("A porta do servidor é inválida.");
            return Create(trimmed[1..closing], bracketPort);
        }
        if (trimmed.Count(character => character == ':') > 1) return Create(trimmed, defaultPort);
        var lastColon = trimmed.LastIndexOf(':');
        if (lastColon >= 0)
        {
            var hostPart = trimmed[..lastColon];
            var portPart = trimmed[(lastColon + 1)..];

            if (int.TryParse(portPart, out var parsedPort) && parsedPort is > 0 and <= 65535)
            {
                return Create(hostPart, parsedPort);
            }

            if (!hostPart.Contains(':'))
            {
                throw new AddressParseException(
                    $"A porta '{portPart}' é inválida. Informe um número entre 1 e 65535.");
            }
        }

        return Create(trimmed, defaultPort);
    }

    public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}
