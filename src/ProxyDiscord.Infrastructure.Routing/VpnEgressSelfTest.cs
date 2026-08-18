using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Diagnostics;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.Routing;

public sealed class VpnEgressSelfTest(TunnelDiagnostics diagnostics, ILogger<VpnEgressSelfTest> logger)
    : IVpnEgressSelfTest
{
    private static readonly TimeSpan PROBE_TIMEOUT = TimeSpan.FromSeconds(8);

    private static readonly (string Host, string Path)[] PUBLIC_IP_ENDPOINTS =
    [
        ("ifconfig.me", "/ip"),
        ("api.ipify.org", "/"),
        ("icanhazip.com", "/"),
    ];

    private static readonly IPAddress DNS_PROBE_SERVER = IPAddress.Parse("8.8.8.8");

    public async Task<EgressSelfTestResult> RunAsync(
        VpnAdapterInfo adapter, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PROBE_TIMEOUT * 3);

        var result = await ProbeAsync(adapter, timeout.Token);
        diagnostics.SelfTestCompleted(result);

        if (result.Success)
        {
            logger.LogInformation("Auto-teste de saída pela VPN: {Summary}", result.Summary);
        }
        else
        {
            logger.LogError("Auto-teste de saída pela VPN falhou: {Summary}", result.Summary);
        }

        return result;
    }

    private async Task<EgressSelfTestResult> ProbeAsync(VpnAdapterInfo adapter, CancellationToken cancellationToken)
    {
        string? throughVpn;
        try
        {
            throughVpn = await GetPublicIpAsync(adapter, cancellationToken);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.NetworkUnreachable)
        {
            return new EgressSelfTestResult(
                false,
                $"a interface VPN {adapter.InterfaceIndex} não tem rota para a internet (NetworkUnreachable). " +
                "A rota do túnel não foi instalada — nenhum tráfego passaria pela VPN.");
        }
        catch (Exception ex)
        {
            return new EgressSelfTestResult(false, $"não foi possível sair pela VPN: {ex.Message}");
        }

        if (throughVpn is null)
        {
            return new EgressSelfTestResult(
                false, "nenhum serviço de IP público respondeu pela VPN; a saída do túnel não pôde ser confirmada.");
        }

        var udpWorks = await TryDnsThroughVpnAsync(adapter, cancellationToken);
        var direct = await TryGetPublicIpDirectAsync(cancellationToken);

        if (direct is not null && string.Equals(direct, throughVpn, StringComparison.OrdinalIgnoreCase))
        {
            return new EgressSelfTestResult(
                false,
                $"o IP público pela VPN ({throughVpn}) é igual ao da conexão direta — o tráfego não está saindo pelo túnel.",
                throughVpn, direct, udpWorks);
        }

        var udpNote = udpWorks ? "UDP ok" : "UDP sem resposta";
        return new EgressSelfTestResult(
            true,
            $"IP público pela VPN {throughVpn} (direto {direct ?? "desconhecido"}), TCP ok, {udpNote}.",
            throughVpn, direct, udpWorks);
    }

    private static async Task<string?> GetPublicIpAsync(VpnAdapterInfo adapter, CancellationToken cancellationToken)
    {
        foreach (var (host, path) in PUBLIC_IP_ENDPOINTS)
        {
            try
            {
                using var socket = VpnBoundSocketFactory.CreateTcpSocket(adapter);
                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
                var target = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);
                if (target is null)
                {
                    continue;
                }

                await socket.ConnectAsync(new IPEndPoint(target, 80), cancellationToken);
                var request = Encoding.ASCII.GetBytes(
                    $"GET {path} HTTP/1.1\r\nHost: {host}\r\nUser-Agent: curl/8\r\nConnection: close\r\n\r\n");
                await socket.SendAsync(request, SocketFlags.None, cancellationToken);

                var response = await ReadAllAsync(socket, cancellationToken);
                var body = ExtractBody(response);
                if (IPAddress.TryParse(body, out var parsed))
                {
                    return parsed.ToString();
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.NetworkUnreachable)
            {
                throw;
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

    private static async Task<string?> TryGetPublicIpDirectAsync(CancellationToken cancellationToken)
    {
        foreach (var (host, path) in PUBLIC_IP_ENDPOINTS)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
                var target = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);
                if (target is null)
                {
                    continue;
                }

                await socket.ConnectAsync(new IPEndPoint(target, 80), cancellationToken);
                var request = Encoding.ASCII.GetBytes(
                    $"GET {path} HTTP/1.1\r\nHost: {host}\r\nUser-Agent: curl/8\r\nConnection: close\r\n\r\n");
                await socket.SendAsync(request, SocketFlags.None, cancellationToken);

                var body = ExtractBody(await ReadAllAsync(socket, cancellationToken));
                if (IPAddress.TryParse(body, out var parsed))
                {
                    return parsed.ToString();
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private async Task<bool> TryDnsThroughVpnAsync(VpnAdapterInfo adapter, CancellationToken cancellationToken)
    {
        try
        {
            using var socket = VpnBoundSocketFactory.CreateUdpSocket(adapter);
            var query = BuildDnsQuery("example.com");
            await socket.SendToAsync(query, SocketFlags.None, new IPEndPoint(DNS_PROBE_SERVER, 53), cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PROBE_TIMEOUT);

            var buffer = new byte[512];
            var received = await socket.ReceiveFromAsync(
                buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);

            return received.ReceivedBytes >= 12 &&
                   buffer[0] == query[0] && buffer[1] == query[1] &&
                   (buffer[2] & 0x80) != 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Sonda DNS UDP pela VPN falhou: {Message}", ex.Message);
            return false;
        }
    }

    private static byte[] BuildDnsQuery(string name)
    {
        var labels = name.Split('.');
        var length = 12 + labels.Sum(label => label.Length + 1) + 1 + 4;
        var query = new byte[length];

        Random.Shared.NextBytes(query.AsSpan(0, 2));
        query[2] = 0x01;
        query[5] = 0x01;

        var offset = 12;
        foreach (var label in labels)
        {
            query[offset++] = (byte)label.Length;
            Encoding.ASCII.GetBytes(label).CopyTo(query, offset);
            offset += label.Length;
        }

        query[offset++] = 0;
        query[offset++] = 0;
        query[offset++] = 1;
        query[offset++] = 0;
        query[offset] = 1;
        return query;
    }

    private static async Task<string> ReadAllAsync(Socket socket, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PROBE_TIMEOUT);

        var buffer = new byte[4096];
        var builder = new StringBuilder();
        while (builder.Length < 16 * 1024)
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
            if (read == 0)
            {
                break;
            }

            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return builder.ToString();
    }

    private static string ExtractBody(string response)
    {
        var separator = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return separator < 0 ? string.Empty : response[(separator + 4)..].Trim();
    }
}
