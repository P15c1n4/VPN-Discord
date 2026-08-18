namespace ProxyDiscord.Infrastructure.WinDivert;

/// <summary>
/// Traduz o código de erro do <c>WinDivertOpen</c> para uma explicação acionável.
/// </summary>
/// <remarks>
/// O driver do WinDivert é um <b>serviço de kernel único da máquina</b>, chamado <c>WinDivert</c>.
/// Quem abre o primeiro handle registra o serviço apontando para o próprio <c>WinDivert64.sys</c>;
/// enquanto ele estiver carregado, outro aplicativo que traga uma build diferente do WinDivert não
/// consegue registrar a dele. Os erros que aparecem nesse cenário não dizem nada sobre isso — o
/// 1072 fala em "serviço marcado para exclusão" e o 1058, em "serviço desabilitado" —, então a
/// tradução acontece aqui, uma vez, para os dois handles.
/// </remarks>
internal static class WinDivertOpenFailure
{
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_INVALID_PARAMETER = 87;
    private const int ERROR_INVALID_IMAGE_HASH = 577;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;
    private const int ERROR_SERVICE_DISABLED = 1058;
    private const int ERROR_DRIVER_BLOCKED = 1275;

    public static string Describe(int win32Error, string filter, string layer) =>
        $"WinDivertOpen (camada {layer}) falhou para o filtro '{filter}': {Explain(win32Error)}";

    private static string Explain(int win32Error) => win32Error switch
    {
        ERROR_FILE_NOT_FOUND =>
            "o WinDivert64.sys não foi encontrado. Ele precisa ficar na mesma pasta do executável — " +
            "copie a pasta inteira do publish, não só o .exe.",

        ERROR_ACCESS_DENIED =>
            "acesso negado. O app precisa rodar como administrador para carregar o driver.",

        ERROR_SERVICE_MARKED_FOR_DELETE =>
            "o serviço de driver 'WinDivert' está marcado para exclusão por outro aplicativo que " +
            "também usa WinDivert. Feche esse aplicativo; se o serviço ficar preso nesse estado " +
            "(sc query WinDivert mostrando STOP_PENDING), só um reinício do Windows libera.",

        ERROR_SERVICE_DISABLED =>
            "o serviço de driver 'WinDivert' está desabilitado — normalmente resquício de outro " +
            "aplicativo que usa WinDivert e ainda não liberou o driver. Feche-o e tente de novo.",

        ERROR_DRIVER_BLOCKED or ERROR_INVALID_IMAGE_HASH =>
            "o Windows bloqueou o driver. Verifique se o antivírus não colocou o WinDivert64.sys em " +
            "quarentena e se a política de assinatura de driver da máquina permite carregá-lo.",

        ERROR_SERVICE_DOES_NOT_EXIST =>
            "o serviço do driver não pôde ser registrado. Confirme que o WinDivert64.sys está junto " +
            "do executável e que o app está elevado.",

        ERROR_INVALID_PARAMETER =>
            "parâmetro inválido — filtro recusado pelo driver, ou o WinDivert.dll e o WinDivert64.sys " +
            "são de versões diferentes.",

        _ => $"erro {win32Error}.",
    };
}
