using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Ports;

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

internal sealed class OpenVpnStateChangedEventArgs(OpenVpnState state, string? message) : EventArgs
{
    public OpenVpnState State { get; } = state;
    public string? Message { get; } = message;
}

internal sealed class OpenVpnManagementClient(
    ILogger logger,
    string? username = null,
    string? password = null,
    string? managementPassword = null,
    IOpenVpnInteractivePrompt? interactivePrompt = null) : IDisposable
{
    private static readonly TimeSpan CONNECT_RETRY_DELAY = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan COMMAND_ACK_TIMEOUT = TimeSpan.FromSeconds(5);

    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile OpenVpnState _state;
    private volatile string? _lastStateMessage;
    private int _credentialRequestCount;

    public OpenVpnState State { get => _state; private set => _state = value; }

    public string? LastStateMessage { get => _lastStateMessage; private set => _lastStateMessage = value; }

    public event EventHandler<OpenVpnStateChangedEventArgs>? StateChanged;

    public async Task<bool> ConnectAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            }
            catch (SocketException)
            {
                client.Dispose();
                await Task.Delay(CONNECT_RETRY_DELAY, cancellationToken);
                continue;
            }
            catch
            {
                client.Dispose();
                throw;
            }

            _client = client;
            var stream = client.GetStream();
            var protocolEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            _reader = new StreamReader(stream, protocolEncoding);
            _writer = new StreamWriter(stream, protocolEncoding) { AutoFlush = true };

            if (!string.IsNullOrEmpty(managementPassword) &&
                !await AuthenticateManagementAsync(managementPassword, cancellationToken))
            {
                return false;
            }

            return await SendCommandAsync("state on", cancellationToken) &&
                   await SendCommandAsync("hold release", cancellationToken);
        }

        return false;
    }

    private async Task<bool> AuthenticateManagementAsync(
        string managementSecret,
        CancellationToken cancellationToken)
    {
        if (managementSecret.Contains('\r') || managementSecret.Contains('\n'))
        {
            logger.LogError("A senha temporária da interface OpenVPN contém caracteres inválidos.");
            return false;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(COMMAND_ACK_TIMEOUT);

        try
        {
            var reader = _reader;
            var writer = _writer;
            if (reader is null || writer is null)
            {
                return false;
            }

            // O protocolo normalmente envia ENTER PASSWORD antes de ler a senha. Enviá-la
            // imediatamente evita depender desse prompt, que pode não chegar em algumas
            // combinações do OpenVPN no Windows. A senha só é aceita se o servidor confirmar.
            await SendAsync(managementSecret);
            while (await reader.ReadLineAsync(timeoutCts.Token) is { } response)
            {
                if (string.Equals(response, "SUCCESS: password is correct", StringComparison.Ordinal))
                {
                    return true;
                }

                if (response.StartsWith("ERROR:", StringComparison.Ordinal))
                {
                    logger.LogWarning("A autenticação da interface de gerenciamento do OpenVPN foi recusada.");
                    return false;
                }

                // ENTER PASSWORD e notificações informativas podem ser enviados antes da
                // confirmação. Não contêm credenciais e não substituem a validação SUCCESS.
            }

            logger.LogWarning("A conexão com a interface de gerenciamento do OpenVPN foi encerrada antes da confirmação.");
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("A interface de gerenciamento do OpenVPN não concluiu a autenticação no prazo.");
            return false;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Falha ao autenticar na interface de gerenciamento do OpenVPN.");
            return false;
        }
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

                await HandleLineAsync(line, timeoutCts.Token);

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

    public async Task MonitorAsync(CancellationToken cancellationToken)
    {
        if (_reader is not { } reader)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested && await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                await HandleLineAsync(line, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "A leitura da interface de gerenciamento do OpenVPN foi encerrada.");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && State is not OpenVpnState.Exiting)
            {
                State = OpenVpnState.Exiting;
                LastStateMessage = "A interface de gerenciamento do OpenVPN foi encerrada.";
                StateChanged?.Invoke(
                    this,
                    new OpenVpnStateChangedEventArgs(State, LastStateMessage));
            }
        }
    }

    private async Task HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        // Management notifications may contain authentication tokens or challenge details.
        // Keep their raw contents out of persistent logs.
        if (!line.StartsWith(">PASSWORD:", StringComparison.Ordinal))
        {
            logger.LogDebug("OpenVPN management event received ({EventType}).", line.Split(':', 2)[0]);
        }

        var previousState = State;

        if (!line.StartsWith(">STATE:", StringComparison.Ordinal))
        {
            if (line.StartsWith(">PASSWORD:Need 'Private Key' password", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePrivateKeyPassphraseAsync(cancellationToken);
            }
            else if (line.StartsWith(">PASSWORD:Need 'Auth' username/password", StringComparison.OrdinalIgnoreCase))
            {
                var hasChallenge = line.IndexOf(" SC:", StringComparison.OrdinalIgnoreCase) >= 0;
                if (hasChallenge && TryGetStaticChallenge(line, out var challengeText, out var challengeEcho))
                {
                    await HandleStaticChallengeAsync(challengeText, challengeEcho, cancellationToken);
                }
                else if (hasChallenge)
                {
                    FailAuthentication("O servidor enviou um desafio de autenticação em formato inválido.");
                }
                else if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                {
                    State = OpenVpnState.AuthFailed;
                    LastStateMessage = "O OpenVPN solicitou credenciais, mas não há credenciais disponíveis em memória.";
                }
                else
                {
                    var requestNumber = Interlocked.Increment(ref _credentialRequestCount);
                    await SendCredentialResponseAsync(username, password);
                    logger.LogDebug(
                        "Credenciais da sessão enviadas à interface OpenVPN para a solicitação {RequestNumber}.",
                        requestNumber);
                }
            }
            else if (line.StartsWith(">PASSWORD:Verification Failed", StringComparison.OrdinalIgnoreCase))
            {
                State = OpenVpnState.AuthFailed;
                LastStateMessage = "O servidor recusou as credenciais de autenticação.";
            }

            NotifyStateChange(previousState);
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

        NotifyStateChange(previousState);
    }

    private async Task HandlePrivateKeyPassphraseAsync(CancellationToken cancellationToken)
    {
        if (interactivePrompt is null)
        {
            FailAuthentication("A chave privada exige uma senha, mas não há uma janela de autenticação disponível.");
            return;
        }

        var response = await interactivePrompt.RequestAsync(
            new OpenVpnPromptRequest(
                OpenVpnPromptKind.PrivateKeyPassphrase,
                "O perfil OpenVPN exige a senha da chave privada. Essa senha será usada somente nesta conexão.",
                null,
                null,
                ChallengeResponseEcho: false),
            cancellationToken);

        if (string.IsNullOrEmpty(response?.Password))
        {
            FailAuthentication("A senha da chave privada não foi informada.");
            return;
        }

        if (ContainsLineBreak(response.Password))
        {
            FailAuthentication("A senha da chave privada contém caracteres não aceitos pelo OpenVPN.");
            return;
        }

        await SendAsync($"password \"Private Key\" {Quote(response.Password)}");
    }

    private async Task HandleStaticChallengeAsync(
        string challengeText,
        bool challengeEcho,
        CancellationToken cancellationToken)
    {
        if (interactivePrompt is null)
        {
            FailAuthentication("O servidor exige uma resposta adicional, mas não há uma janela de autenticação disponível.");
            return;
        }

        var response = await interactivePrompt.RequestAsync(
            new OpenVpnPromptRequest(
                OpenVpnPromptKind.StaticChallenge,
                challengeText,
                username,
                password,
                challengeEcho),
            cancellationToken);

        if (string.IsNullOrEmpty(response?.Username) || response.Password is null ||
            response.ChallengeResponse is null)
        {
            FailAuthentication("A autenticação adicional foi cancelada ou está incompleta.");
            return;
        }

        if (ContainsLineBreak(response.Username) || ContainsLineBreak(response.Password) ||
            ContainsLineBreak(response.ChallengeResponse))
        {
            FailAuthentication("As credenciais contêm caracteres não aceitos pelo OpenVPN.");
            return;
        }

        var encodedPassword = Convert.ToBase64String(Encoding.UTF8.GetBytes(response.Password));
        var encodedChallenge = Convert.ToBase64String(Encoding.UTF8.GetBytes(response.ChallengeResponse));
        await SendAsync($"username \"Auth\" {Quote(response.Username)}");
        await SendAsync($"password \"Auth\" {Quote($"SCRV1:{encodedPassword}:{encodedChallenge}")}");
    }

    private static bool TryGetStaticChallenge(string line, out string challengeText, out bool echo)
    {
        challengeText = string.Empty;
        echo = false;
        var marker = line.IndexOf(" SC:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return false;
        }

        var parts = line[(marker + 4)..].Split(':', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        echo = parts[0].IndexOf('E') >= 0 || parts[0].IndexOf('e') >= 0;
        challengeText = parts[1];
        return true;
    }

    private void FailAuthentication(string message)
    {
        State = OpenVpnState.AuthFailed;
        LastStateMessage = message;
    }

    private static bool ContainsLineBreak(string value) => value.Contains('\r') || value.Contains('\n');

    private void NotifyStateChange(OpenVpnState previousState)
    {
        if (State != previousState)
        {
            StateChanged?.Invoke(this, new OpenVpnStateChangedEventArgs(State, LastStateMessage));
        }
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

    private async Task SendCredentialResponseAsync(string user, string secret)
    {
        if (user.Contains('\r') || user.Contains('\n') || secret.Contains('\r') || secret.Contains('\n'))
        {
            State = OpenVpnState.AuthFailed;
            LastStateMessage = "As credenciais contêm caracteres de controle não aceitos pelo OpenVPN.";
            return;
        }

        await SendAsync($"username \"Auth\" {Quote(user)}");
        await SendAsync($"password \"Auth\" {Quote(secret)}");
    }

    private static string Quote(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private async Task SendAsync(string command)
    {
        if (_writer is { } writer)
        {
            await _writeGate.WaitAsync();
            try
            {
                await writer.WriteLineAsync(command);
            }
            finally
            {
                _writeGate.Release();
            }
        }
    }

    private async Task<bool> SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        await SendAsync(command);

        if (_reader is not { } reader)
        {
            return false;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(COMMAND_ACK_TIMEOUT);

        try
        {
            while (await reader.ReadLineAsync(timeoutCts.Token) is { } line)
            {
                await HandleLineAsync(line, timeoutCts.Token);

                if (line.StartsWith("SUCCESS:", StringComparison.Ordinal))
                {
                    return true;
                }

                if (line.StartsWith("ERROR:", StringComparison.Ordinal))
                {
                    logger.LogWarning("O OpenVPN recusou o comando '{Command}' da interface de gerenciamento.", command);
                    return false;
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

        return false;
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Dispose();
        _reader = null;
        _writer = null;
        _client = null;
        _writeGate.Dispose();
    }
}
