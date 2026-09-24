using System.Diagnostics;
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.OpenVpn;

internal sealed class OpenVpnConnection(
    OpenVpnBinaries binaries,
    TapAdapterProvisioner adapterProvisioner,
    OpenVpnProfileWriter profileWriter,
    IVpnInterfaceGatewayResolver gatewayResolver,
    ILogger<OpenVpnConnection> logger) : IVpnProvider
{
    private static readonly TimeSpan MANAGEMENT_CONNECT_TIMEOUT = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CONNECT_TIMEOUT = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SHUTDOWN_GRACE = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TUNNEL_INFO_TIMEOUT = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();

    private Process? _process;
    private OpenVpnManagementClient? _management;
    private OpenVpnProfile? _profile;
    private VpnAdapterInfo? _adapterInfo;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _connectionReady;
    private bool _stopping;
    private bool _lossRaised;

    public event EventHandler<VpnConnectionLostEventArgs>? ConnectionLost;

    public VpnProtocol Protocol => VpnProtocol.OpenVpn;

    public async Task<VpnConnectionResult> ConnectAsync(
        VpnConnectionRequest request, CancellationToken cancellationToken = default)
    {
        if (!binaries.IsAvailable)
        {
            return VpnConnectionResult.Failed(
                VpnLinkStatus.Error,
                $"Os arquivos necessários do OpenVPN não foram encontrados. Detalhes: {binaries.DescribeMissing()}");
        }

        if (string.IsNullOrWhiteSpace(request.OpenVpnConfigBase64))
        {
            return VpnConnectionResult.Failed(
                VpnLinkStatus.Error,
                "Este servidor não oferece um perfil OpenVPN. Escolha outro servidor ou use MS-SSTP.");
        }

        await DisconnectAsync(cancellationToken);

        lock (_lock)
        {
            _stopping = false;
            _lossRaised = false;
        }

        try
        {
            var adapterName = await adapterProvisioner.EnsureAdapterAsync(cancellationToken);
            var managementPort = ReserveLoopbackPort();

            var profile = profileWriter.Write(
                request.OpenVpnConfigBase64,
                request.Username,
                request.Password,
                adapterName,
                managementPort,
                request.UseProfileOpenVpnCredentials);

            lock (_lock)
            {
                _profile = profile;
            }

            var console = new StringBuilder();
            var process = StartOpenVpn(profile, console);
            lock (_lock)
            {
                _process = process;
            }

            var management = new OpenVpnManagementClient(logger);
            lock (_lock)
            {
                _management = management;
            }

            if (!await management.ConnectAsync(managementPort, MANAGEMENT_CONNECT_TIMEOUT, cancellationToken))
            {
                return await FailAsync(
                    DescribeStartupFailure(process, console, profile), profile, cancellationToken);
            }

            var state = await management.WaitForConnectedAsync(CONNECT_TIMEOUT, cancellationToken);
            if (state != OpenVpnState.Connected)
            {
                return await FailAsync(DescribeFailure(state, profile), profile, cancellationToken);
            }

            var adapterInfo = await ResolveAdapterInfoAsync(profile, adapterName, cancellationToken);
            if (adapterInfo is null)
            {
                return await FailAsync(
                    "O OpenVPN conectou, mas não foi possível obter o endereço IP do túnel.",
                    profile,
                    cancellationToken);
            }

            lock (_lock)
            {
                _adapterInfo = adapterInfo;
                _connectionReady = true;
                _monitorCts = new CancellationTokenSource();
                management.StateChanged += OnManagementStateChanged;
                _monitorTask = management.MonitorAsync(_monitorCts.Token);
            }

            logger.LogInformation(
                "OpenVPN conectado: interface {IfIdx}, IP {Ip}, gateway {Gateway}",
                adapterInfo.InterfaceIndex, adapterInfo.LocalIp, adapterInfo.GatewayIp ?? "(on-link)");

            return VpnConnectionResult.Ok(VpnLinkStatus.Connected);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao conectar via OpenVPN");
            await DisconnectAsync(CancellationToken.None);
            return VpnConnectionResult.Failed(VpnLinkStatus.Error, $"Não foi possível conectar pelo OpenVPN. Detalhes: {ex.Message}");
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Process? process;
        OpenVpnManagementClient? management;
        OpenVpnProfile? profile;
        CancellationTokenSource? monitorCts;
        Task? monitorTask;

        lock (_lock)
        {
            process = _process;
            management = _management;
            profile = _profile;
            _process = null;
            _management = null;
            _profile = null;
            _adapterInfo = null;
            _connectionReady = false;
            _stopping = true;
            monitorCts = _monitorCts;
            monitorTask = _monitorTask;
            _monitorCts = null;
            _monitorTask = null;
        }

        if (management is not null)
        {
            management.StateChanged -= OnManagementStateChanged;
        }

        monitorCts?.Cancel();

        if (monitorTask is not null)
        {
            try
            {
                await monitorTask;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                logger.LogDebug(ex, "Monitor do OpenVPN encerrado durante a desconexão.");
            }
        }

        monitorCts?.Dispose();

        if (management is not null)
        {
            await management.RequestShutdownAsync();
        }

        if (process is not null)
        {
            await WaitOrKillAsync(process);
            process.Dispose();
        }

        management?.Dispose();
        profile?.Dispose();
    }

    public Task<VpnLinkStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_process is null || _process.HasExited)
            {
                return Task.FromResult(VpnLinkStatus.Disconnected);
            }

            return Task.FromResult(
                _management?.State == OpenVpnState.Connected ? VpnLinkStatus.Connected : VpnLinkStatus.Connecting);
        }
    }

    public Task<VpnAdapterInfo?> GetAdapterInfoAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_adapterInfo);
        }
    }

    public async Task ForceDisconnectByNameAsync(string entryName, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync(cancellationToken);

        foreach (var stray in Process.GetProcessesByName("openvpn"))
        {
            try
            {
                if (!string.Equals(stray.MainModule?.FileName, binaries.OpenVpnExe, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                logger.LogWarning("Encerrando processo OpenVPN órfão (PID {Pid}) de uma execução anterior.", stray.Id);
                stray.Kill(entireProcessTree: true);
                await stray.WaitForExitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Não foi possível inspecionar/encerrar o processo OpenVPN {Pid}", stray.Id);
            }
            finally
            {
                stray.Dispose();
            }
        }
    }

    private Process StartOpenVpn(OpenVpnProfile profile, StringBuilder console)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = binaries.OpenVpnExe,
            WorkingDirectory = profile.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(profile.ConfigPath);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => AppendConsole(console, e.Data);
        process.ErrorDataReceived += (_, e) => AppendConsole(console, e.Data);
        process.Exited += (_, _) => OnProcessExited(process);

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("O Windows não conseguiu iniciar o OpenVPN.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        logger.LogInformation("OpenVPN iniciado (PID {Pid}) com o perfil {Config}", process.Id, profile.ConfigPath);
        return process;
    }

    private void OnManagementStateChanged(object? sender, OpenVpnStateChangedEventArgs args)
    {
        if (args.State is OpenVpnState.Connected or OpenVpnState.Connecting)
        {
            return;
        }

        NotifyConnectionLost($"O OpenVPN mudou para o estado {args.State}: {args.Message ?? "sem detalhe"}.");
    }

    private void OnProcessExited(Process process)
    {
        int? exitCode = null;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }

        NotifyConnectionLost(
            $"O processo OpenVPN encerrou inesperadamente{(exitCode is null ? string.Empty : $" (código {exitCode})") }.");
    }

    private void NotifyConnectionLost(string reason)
    {
        lock (_lock)
        {
            if (_stopping || !_connectionReady || _lossRaised)
            {
                return;
            }

            _lossRaised = true;
        }

        logger.LogWarning("Conexão OpenVPN perdida: {Reason}", reason);
        ConnectionLost?.Invoke(this, new VpnConnectionLostEventArgs(reason));
    }

    private static void AppendConsole(StringBuilder console, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (console)
        {
            console.AppendLine(line.Trim());
        }
    }

    private string DescribeStartupFailure(Process process, StringBuilder console, OpenVpnProfile profile)
    {
        string consoleText;
        lock (console)
        {
            consoleText = console.ToString().Trim();
        }

        var exited = process.HasExited;
        var detail = !string.IsNullOrEmpty(consoleText)
            ? consoleText.Replace(Environment.NewLine, " | ")
            : ReadLogTail(profile.LogPath);

        var message = exited
            ? $"O OpenVPN encerrou com o código {process.ExitCode} antes de iniciar a interface de gerenciamento."
            : "A interface de gerenciamento do OpenVPN não respondeu.";

        return string.IsNullOrWhiteSpace(detail) ? message : $"{message} Detalhes: {detail}";
    }

    private async Task<VpnAdapterInfo?> ResolveAdapterInfoAsync(
        OpenVpnProfile profile, string adapterName, CancellationToken cancellationToken)
    {
        var tunnel = await ReadTunnelInfoAsync(profile.TunnelInfoPath, cancellationToken);

        var adapter = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(nic => string.Equals(nic.Name, adapterName, StringComparison.OrdinalIgnoreCase));

        if (adapter is null)
        {
            logger.LogWarning("Adaptador '{Name}' não encontrado após a conexão do OpenVPN.", adapterName);
            return null;
        }

        var properties = adapter.GetIPProperties();
        var localIp = tunnel.GetValueOrDefault("ifconfig_local")
                      ?? properties.UnicastAddresses
                          .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address
                          .ToString();

        if (localIp is null)
        {
            return null;
        }

        var gatewayCandidates = new[]
        {
            tunnel.GetValueOrDefault("route_vpn_gateway"),
            tunnel.GetValueOrDefault("ifconfig_remote"),
        }.Concat(
            properties.GatewayAddresses
                .Where(gatewayAddress => gatewayAddress.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(gatewayAddress => gatewayAddress.Address.ToString()));

        var gateway = FirstUsableAddress(gatewayCandidates.ToArray());

        gateway ??= gatewayResolver.ResolveGateway((uint)properties.GetIPv4Properties().Index)?.ToString();

        if (gateway is null &&
            InferTopologySubnetGateway(localIp, tunnel.GetValueOrDefault("ifconfig_netmask")) is { } inferredGateway)
        {
            gateway = inferredGateway;
            logger.LogWarning(
                "O OpenVPN não publicou route_vpn_gateway; gateway {Gateway} inferido de {LocalIp}/{Netmask} " +
                "como primeiro host da sub-rede topology subnet.",
                gateway, localIp, tunnel.GetValueOrDefault("ifconfig_netmask"));
        }

        return new VpnAdapterInfo(
            localIp,
            (uint)properties.GetIPv4Properties().Index,
            SubInterfaceIndex: 0,
            gateway);
    }

    private static string? FirstUsableAddress(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) &&
            IPAddress.TryParse(candidate, out var parsed) &&
            !parsed.Equals(IPAddress.Any));

    private static string? InferTopologySubnetGateway(string localIpText, string? netmaskText)
    {
        if (!IPAddress.TryParse(localIpText, out var localIp) ||
            localIp.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.TryParse(netmaskText, out var netmask) ||
            netmask.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var local = BinaryPrimitives.ReadUInt32BigEndian(localIp.GetAddressBytes());
        var mask = BinaryPrimitives.ReadUInt32BigEndian(netmask.GetAddressBytes());
        var hostMask = ~mask;

        // A máscara precisa ser contígua e deixar pelo menos dois hosts utilizáveis.
        if ((hostMask & (hostMask + 1)) != 0 || hostMask < 3)
        {
            return null;
        }

        var network = local & mask;
        var broadcast = network | hostMask;
        var candidate = network + 1;
        if (candidate == local || candidate >= broadcast)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, candidate);
        return new IPAddress(bytes).ToString();
    }

    private async Task<Dictionary<string, string>> ReadTunnelInfoAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TUNNEL_INFO_TIMEOUT;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (File.Exists(path))
            {
                try
                {
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
                    {
                        var separator = line.IndexOf('=');
                        if (separator > 0)
                        {
                            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                        }
                    }

                    logger.LogDebug("Parâmetros do túnel OpenVPN: {Values}",
                        string.Join(", ", values.Select(pair => $"{pair.Key}={pair.Value}")));
                    return values;
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        logger.LogWarning("O script 'up' do OpenVPN não publicou os parâmetros do túnel a tempo.");
        return [];
    }

    private string DescribeFailure(OpenVpnState state, OpenVpnProfile profile)
    {
        var tail = ReadLogTail(profile.LogPath);
        var reason = state switch
        {
            OpenVpnState.AuthFailed => "o servidor recusou a autenticação. Confira usuário e senha",
            OpenVpnState.Exiting => "o OpenVPN encerrou antes de concluir a conexão",
            OpenVpnState.Reconnecting => "o OpenVPN não conseguiu estabelecer a conexão e está tentando novamente",
            _ => "o tempo limite para estabelecer a conexão foi atingido",
        };

        return string.IsNullOrWhiteSpace(tail)
            ? $"Não foi possível conectar pelo OpenVPN: {reason}."
            : $"Não foi possível conectar pelo OpenVPN: {reason}. Detalhes do log: {tail}";
    }

    private string ReadLogTail(string logPath, int lines = 6)
    {
        try
        {
            if (!File.Exists(logPath))
            {
                return string.Empty;
            }

            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" | ", all.TakeLast(lines).Select(line => line.Trim()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private async Task<VpnConnectionResult> FailAsync(
        string message, OpenVpnProfile profile, CancellationToken cancellationToken)
    {
        logger.LogError("{Message}", message);
        await DisconnectAsync(cancellationToken);
        return VpnConnectionResult.Failed(VpnLinkStatus.Error, message);
    }

    private async Task WaitOrKillAsync(Process process)
    {
        try
        {
            using var cts = new CancellationTokenSource(SHUTDOWN_GRACE);
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                logger.LogWarning("O OpenVPN não encerrou em {Seconds}s; finalizando.", SHUTDOWN_GRACE.TotalSeconds);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or SystemException)
            {
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static int ReserveLoopbackPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
