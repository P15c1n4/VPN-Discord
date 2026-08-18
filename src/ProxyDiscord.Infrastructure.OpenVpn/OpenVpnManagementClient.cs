using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ProxyDiscord.Infrastructure.OpenVpn;

internal enum OpenVpnState
{
    Unknown,
    Connecting,
    Connected,
    Reconnecting,
    Exiting,
    AuthFailed,
}

internal sealed class OpenVpnManagementClient(ILogger logger) : IDisposable
{
    private static readonly TimeSpan CONNECT_RETRY_DELAY = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan COMMAND_ACK_TIMEOUT = TimeSpan.FromSeconds(5);

    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public OpenVpnState State { get; private set; } = OpenVpnState.Unknown;

    public string? LastStateMessage { get; private set; }

    public async Task<bool> ConnectAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);

                _client = client;
                var stream = client.GetStream();
                _reader = new StreamReader(stream, Encoding.ASCII);
                _writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

                await SendCommandAsync("state on", cancellationToken);
                await SendCommandAsync("hold release", cancellationToken);
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(CONNECT_RETRY_DELAY, cancellationToken);
            }
        }

        return false;
    }

    public async Task<OpenVpnState> WaitForConnectedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            while (!timeoutCts.IsCancellationRequested && _reader is { } reader)
            {
                var line = await reader.ReadLineAsync(timeoutCts.Token);
                if (line is null)
                {
                    break;
                }

                HandleLine(line);

                if (State is OpenVpnState.Connected or OpenVpnState.AuthFailed or OpenVpnState.Exiting)
                {
                    return State;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }

        return State;
    }

    private void HandleLine(string line)
    {
        logger.LogDebug("openvpn management: {Line}", line);

        if (!line.StartsWith(">STATE:", StringComparison.Ordinal))
        {
            if (line.StartsWith(">PASSWORD:Verification Failed", StringComparison.OrdinalIgnoreCase))
            {
                State = OpenVpnState.AuthFailed;
                LastStateMessage = line;
            }

            return;
        }

        var fields = line[">STATE:".Length..].Split(',');
        if (fields.Length < 2)
        {
            return;
        }

        LastStateMessage = line;
        State = fields[1] switch
        {
            "CONNECTED" => OpenVpnState.Connected,
            "RECONNECTING" => OpenVpnState.Reconnecting,
            "EXITING" => OpenVpnState.Exiting,
            "AUTH" or "GET_CONFIG" or "ASSIGN_IP" or "ADD_ROUTES" or "WAIT" or "RESOLVE" or "TCP_CONNECT" =>
                OpenVpnState.Connecting,
            _ => State,
        };
    }

    public async Task RequestShutdownAsync()
    {
        try
        {
            await SendAsync("signal SIGTERM");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private async Task SendAsync(string command)
    {
        if (_writer is { } writer)
        {
            await writer.WriteLineAsync(command);
        }
    }

    private async Task SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        await SendAsync(command);

        if (_reader is not { } reader)
        {
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(COMMAND_ACK_TIMEOUT);

        try
        {
            while (await reader.ReadLineAsync(timeoutCts.Token) is { } line)
            {
                HandleLine(line);

                if (line.StartsWith("SUCCESS:", StringComparison.Ordinal) ||
                    line.StartsWith("ERROR:", StringComparison.Ordinal))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("O OpenVPN não confirmou o comando '{Command}' da interface de gerenciamento.", command);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Dispose();
        _reader = null;
        _writer = null;
        _client = null;
    }
}
