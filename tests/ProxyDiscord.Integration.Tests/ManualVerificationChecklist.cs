namespace ProxyDiscord.Integration.Tests;

[Trait("Category", "RequiresAdmin")]
public class ManualVerificationChecklist
{
    [Fact(Skip = "Requer discagem real contra um servidor VPN Gate ao vivo. Procedimento: rodar o app " +
                 "elevado, escolher um servidor MS-SSTP da lista, clicar Conectar, e confirmar que o " +
                 "auto-teste de saída passa (o painel de Diagnóstico deve mostrar um IP público " +
                 "diferente do IP direto).")]
    public void SstpVpnConnection_DialsRealServer_AndTheEgressSelfTestPasses()
    {
    }

    [Fact(Skip = "Requer discagem real com OpenVPN, incluindo a instalação silenciosa do driver TAP na " +
                 "primeira execução. Procedimento: escolher um servidor com 'OpenVPN' na coluna " +
                 "Protocolos, conectar, e confirmar em 'Get-NetAdapter' que o adaptador 'ProxyDiscord " +
                 "Tunnel' foi criado e está ativo. Repetir com um servidor 'OpenVPN (UDP)'.")]
    public void OpenVpnConnection_ProvisionsTheTapAdapterAndConnects_OverBothTcpAndUdp()
    {
    }

    [Fact(Skip = "Requer verificar a tabela de rotas com a VPN conectada. Procedimento: " +
                 "'Get-NetRoute -InterfaceIndex <ifVPN>' deve listar 0.0.0.0/0 com métrica 9000, e " +
                 "'Get-NetRoute -DestinationPrefix 0.0.0.0/0' deve continuar elegendo a interface " +
                 "física para o tráfego normal. Esta é a rota cuja ausência fazia o túnel inteiro " +
                 "falhar silenciosamente com WSAENETUNREACH.")]
    public void TunnelRoute_ExistsOnTheVpnInterface_WithoutStealingTheMachinesDefaultRoute()
    {
    }

    [Fact(Skip = "Requer o processo alvo (Discord.exe) gerando tráfego TCP real simultaneamente com " +
                 "outro processo (um navegador). Procedimento: conectar com Discord.exe selecionado, " +
                 "abrir o Discord, e confirmar no painel de Diagnóstico que 'Redirecionado para o " +
                 "relay' e 'Saiu pela VPN' sobem para TCP; em paralelo, um site de 'qual é meu IP' no " +
                 "navegador deve continuar mostrando o IP normal.")]
    public void ProcessRoutingEngine_TcpFromTheTargetOnly_ExitsThroughTheVpn()
    {
    }

    [Fact(Skip = "Requer tráfego UDP real: entrar numa call de voz do Discord. TCP funcionar não prova " +
                 "nada sobre UDP — são caminhos distintos no relay. Procedimento: com a call ativa, o " +
                 "painel de Diagnóstico deve mostrar 'Redirecionado para o relay: UDP > 0' e bytes de " +
                 "descida em UDP. Confirmar com 'Get-NetUDPEndpoint' que o ProxyDiscord tem sockets UDP " +
                 "no IP da VPN.")]
    public void ProcessRoutingEngine_UdpVoiceTraffic_ExitsThroughTheVpn()
    {
    }

    [Fact(Skip = "Requer matar o processo do app (Gerenciador de Tarefas) no meio de uma sessão ativa e " +
                 "reabri-lo, confirmando via 'Get-VpnConnection', 'Get-NetRoute' e 'Get-Process openvpn' " +
                 "que nem conexão VPN, nem rota de túnel, nem cliente OpenVPN ficaram para trás. A " +
                 "lógica de decisão já é coberta por teste automatizado com um IProcessLivenessChecker " +
                 "falso; o que falta aqui é a limpeza real.")]
    public void AppKilledMidSession_NextStartupCleansUpVpnRouteAndOpenVpnProcess()
    {
    }
}
