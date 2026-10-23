using System.Text;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Routing;

internal sealed class ProxiFyreLogSession
{
    private readonly string _directory;
    private readonly string _endpointMarker;
    private readonly TunnelProtocolScope _scope;
    private readonly Dictionary<string, (long Position, string Partial)> _positions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _tail = new();
    private readonly object _lock = new();
    private bool _tcpReady;
    private bool _udpReady;
    private bool _routerReady;

    internal ProxiFyreLogSession(string directory, int socksPort, TunnelProtocolScope scope)
    {
        _directory = directory;
        _endpointMarker = $"for 127.0.0.1:{socksPort} is listening port:";
        _scope = scope;
        Directory.CreateDirectory(directory);
        foreach (var path in LogFiles()) _positions[path] = (new FileInfo(path).Length, "");
    }

    private IEnumerable<string> LogFiles() => Directory.EnumerateFiles(_directory)
        .Where(path => Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                       Path.GetExtension(path).Equals(".log", StringComparison.OrdinalIgnoreCase));

    internal bool ReadReady()
    {
        lock (_lock)
        {
            foreach (var path in LogFiles())
            {
                try
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var cursor = _positions.GetValueOrDefault(path);
                    if (file.Length < cursor.Position) cursor = (0, "");
                    file.Position = cursor.Position;
                    using var reader = new StreamReader(file, Encoding.UTF8);
                    var text = cursor.Partial + reader.ReadToEnd();
                    var lastNewLine = text.LastIndexOf('\n');
                    if (lastNewLine >= 0)
                    {
                        foreach (var line in text[..lastNewLine].Split('\n')) ObserveLine(line.TrimEnd('\r'));
                    }
                    _positions[path] = (file.Position, text[(lastNewLine + 1)..]);
                }
                catch (IOException) { }
            }

            return _routerReady && (!_scope.Includes(TransportProtocol.Tcp) || _tcpReady) &&
                   (!_scope.Includes(TransportProtocol.Udp) || _udpReady);
        }
    }

    private void ObserveLine(string line)
    {
        _tail.Enqueue(line);
        while (_tail.Count > 8) _tail.Dequeue();
        if (line.Contains(_endpointMarker, StringComparison.Ordinal))
        {
            _tcpReady |= line.Contains("Local IPv4 TCP proxy", StringComparison.Ordinal);
            _udpReady |= line.Contains("Local IPv4 UDP proxy", StringComparison.Ordinal);
        }
        if ((_tcpReady || _udpReady) &&
            line.Contains("SOCKS5 Local Router instance started successfully.", StringComparison.Ordinal))
            _routerReady = true;
    }

    internal string ReadTail()
    {
        lock (_lock)
        {
            try { ReadReady(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Não foi possível ler o log do ProxiFyre: {ex.Message}";
            }
            return _tail.Count == 0 ? "Nenhuma mensagem desta tentativa foi gravada no log do ProxiFyre." : string.Join(" | ", _tail);
        }
    }

    internal string DescribeExit(int exitCode) =>
        $"O ProxiFyre encerrou (código {exitCode}; 0x{unchecked((uint)exitCode):X8}). {ReadTail()}";

    internal async Task WaitUntilReadyAsync(Func<bool> hasExited, Func<int> exitCode,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (hasExited()) throw new InvalidOperationException(DescribeExit(exitCode()));
                if (ReadReady() && !hasExited()) return;
                await Task.Delay(200, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"O ProxiFyre não confirmou a inicialização no prazo. {ReadTail()}");
        }
    }
}
