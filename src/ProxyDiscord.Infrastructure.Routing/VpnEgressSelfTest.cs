using System.Net;
using System.Net.Sockets;
using System.Net.Security;
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

    private static readonly (string Host, int Port, string Path, bool UseTls)[] PUBLIC_IP_ENDPOINTS =
    [
        ("ifconfig.me", 443, "/ip", true),
        ("api.ipify.org", 443, "/", true),
        ("icanhazip.com", 443, "/", true),
        ("ifconfig.me", 80, "/ip", false),
        ("api.ipify.org", 80, "/", false),
        ("icanhazip.com", 80, "/", false),
    ];

    private static readonly IPAddress DNS_PROBE_SERVER = IPAddress.Parse("8.8.8.8");

    public async Task<EgressSelfTestResult> RunAsync(
        VpnAdapterInfo adapter,
        OutboundInterfaceInfo? directInterface = null,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PROBE_TIMEOUT * 3);

        var result = await ProbeAsync(adapter, directInterface, timeout.Token);
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

    private async Task<EgressSelfTestResult> ProbeAsync(
        VpnAdapterInfo adapter,
        OutboundInterfaceInfo? directInterface,
        CancellationToken cancellationToken)
    {
        var vpnProbe = await GetPublicIpAsync(
            socketFactory: () => VpnBoundSocketFactory.CreateTcpSocket(adapter),
            bindingDescription: $"VPN if {adapter.InterfaceIndex} ({adapter.LocalIp})",
            cancellationToken);

        if (vpnProbe.Ip is null)
        {
            return new EgressSelfTestResult(
                false,
                $"Nenhum serviço de IP público respondeu pela VPN. Não foi possível confirmar a saída do túnel. " +
                vpnProbe.Details);
        }

        var udpWorks = await TryDnsThroughVpnAsync(adapter, cancellationToken);
        PublicIpProbeResult directProbe = new(null, "Consulta direta não executada.");

        if (directInterface is not null)
        try
        {
            directProbe = await GetPublicIpAsync(
                socketFactory: () => VpnBoundSocketFactory.CreateTcpSocket(directInterface),
                bindingDescription: $"direto if {directInterface.InterfaceIndex} ({directInterface.LocalIp})",
                cancellationToken);
        }
        catch (Exception ex)
        {
            directProbe = new PublicIpProbeResult(null, $"falha inesperada: {ex.Message}");
        }

        var directIp = directProbe.Ip;
        var comparisonNote = directInterface is null
            ? "A interface física não foi identificada; comparação direta ignorada."
            : directIp is null
                ? $"A consulta direta não respondeu ({directProbe.Details})."
                : string.Equals(directIp, vpnProbe.Ip, StringComparison.OrdinalIgnoreCase)
                    ? $"Aviso: o IP direto é igual ao da VPN ({vpnProbe.Ip}). Isso não reprova a conexão."
                    : $"IP direto diferente: {directIp}.";

        if (directIp is not null && string.Equals(directIp, vpnProbe.Ip, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "O IP público direto e o IP pela VPN coincidiram ({Ip}). O autoteste continuará aprovado porque " +
                "a consulta VPN foi presa à interface {VpnIfIdx}.",
                vpnProbe.Ip, adapter.InterfaceIndex);
        }

        var udpNote = udpWorks ? "UDP: OK" : "UDP: sem resposta";
        return new EgressSelfTestResult(
            true,
            $"IP de saída pela VPN: {vpnProbe.Ip}. TCP: OK. {udpNote}. " +
            comparisonNote,
            vpnProbe.Ip, directIp, udpWorks);
    }

    private static async Task<PublicIpProbeResult> GetPublicIpAsync(
        Func<Socket> socketFactory,
        string bindingDescription,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();

        foreach (var (host, port, path, useTls) in PUBLIC_IP_ENDPOINTS)
        {
            try
            {
                using var socket = socketFactory();
                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
                var target = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);
                if (target is null)
                {
                    continue;
                }

                await socket.ConnectAsync(new IPEndPoint(target, port), cancellationToken);
                await using var networkStream = new NetworkStream(socket, ownsSocket: false);
                using var tls = useTls
                    ? new SslStream(networkStream, leaveInnerStreamOpen: true)
                    : null;
                Stream stream = tls is null ? networkStream : tls;
                if (tls is not null)
                {
                    await tls.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions { TargetHost = host },
                        cancellationToken);
                }

                var request = Encoding.ASCII.GetBytes(
                    $"GET {path} HTTP/1.1\r\nHost: {host}\r\nUser-Agent: curl/8\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(request, cancellationToken);

                var response = await ReadAllAsync(stream, cancellationToken);
                var body = ExtractBody(response);
                if (IPAddress.TryParse(body, out var parsed))
                {
                    return new PublicIpProbeResult(parsed.ToString(),
                        $"{bindingDescription} respondeu via {host}:{port}");
                }

                failures.Add($"{host}:{port}: resposta sem IP público");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.NetworkUnreachable)
            {
                failures.Add($"{host}:{port}: NetworkUnreachable");
            }
            catch (Exception ex)
            {
                failures.Add($"{host}:{port}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return new PublicIpProbeResult(null, string.Join("; ", failures));
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

    private static async Task<string> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PROBE_TIMEOUT);

        var buffer = new byte[4096];
        var builder = new StringBuilder();
        while (builder.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(buffer, timeout.Token);
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

    private sealed record PublicIpProbeResult(string? Ip, string Details);
}
