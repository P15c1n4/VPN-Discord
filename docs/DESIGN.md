# Discord-VPN — Notas de arquitetura e decisões técnicas

Aplicativo Windows (.NET 8 / WPF) que roteia o tráfego de **um único processo escolhido**
(padrão `Discord.exe`) através de uma VPN — **OpenVPN** ou **MS-SSTP** —, deixando todos os
demais processos na rota normal do Windows. Integra a lista pública de servidores do VPN Gate.

Este documento registra o que foi descoberto durante a pesquisa técnica, as decisões de
arquitetura resultantes, os padrões de projeto usados e os problemas encontrados/corrigidos
durante a implementação. É o entregável pedido explicitamente pelo usuário para acompanhar
o código.

## 0. A falha que motivou a reescrita: `IP_UNICAST_IF` não cria rota

O app conectava a VPN, o motor de roteamento iniciava, o `ProcessGroupWatcher` reportava
"6 processos sendo tunelados" — e **nenhum byte passava pela VPN**. Sem erro no log.

O `VpnBoundSocketFactory` afirmava, no próprio comentário, que `IP_UNICAST_IF` "força o tráfego
por uma interface *independentemente da tabela de rotas*, que é exatamente o necessário aqui, já
que a VPN é configurada com split tunneling e **não possui rota padrão**".

**Essa premissa está errada.** `IP_UNICAST_IF` *restringe* a busca de rota àquela interface — ele
não cria rota nenhuma. Sem rota na interface, a busca falha.

Medido nesta máquina, com uma sessão SSTP ativa (interface PPP 34, IP 10.211.1.4):

```
sem pin      -> 1.1.1.1:443    : OK, origem 192.168.15.2
ifidx 5      -> 1.1.1.1:443    : OK, origem 192.168.15.2      (placa física)
ifidx 34     -> 1.1.1.1:443    : FAIL NetworkUnreachable (10051)   <-- a VPN
ifidx 34     -> 10.211.1.1:443 : TIMEOUT (a rota existe; o host é que não responde)
```

A tabela de rotas da interface 34 tinha apenas `10.211.0.0/16`, `10.0.0.0/8` e o próprio
`/32` — nenhuma `0.0.0.0/0`. Ou seja: **toda** conexão que o relay abria morria em
`ConnectAsync` com `WSAENETUNREACH`. E o `TcpTunnelRelay` capturava `SocketException` em um
`logger.LogDebug`, que o `CompositionRoot` filtrava (`SetMinimumLevel(LogLevel.Information)`).
Daí o sintoma exato: túnel "armado", zero tráfego, zero erro.

A correção está na §1.2. O que este caso ensina, e que moldou o resto do trabalho:

* **Um teste verde não prova que o tráfego anda.** Toda a suíte do motor passava contra um
  handle falso enquanto nada era tunelado. Por isso agora existe o `VpnEgressSelfTest` (§1.7),
  que roda *antes* de o app dizer "Conectado".
* **Erro engolido em `Debug` é erro invisível.** O nível mínimo de log passou para `Debug` e as
  falhas de socket do relay viraram `Warning` com o `SocketErrorCode` explícito.

## 1. Limitações técnicas confirmadas e decisões tomadas

### 1.1 WinDivert não expõe `processId` na camada NETWORK — daí o desenho de dois handles

O campo `processId` do filtro do WinDivert existe apenas nas camadas **FLOW**, **SOCKET** e
**REFLECT**; a documentação é explícita em dizer que limitações técnicas impedem oferecê-lo nas
camadas `WINDIVERT_LAYER_NETWORK*`. Isso é verificado por teste, contra o parser real do
WinDivert, em `WinDivertFilterSyntaxTests`.

A primeira versão contornava isso consultando as tabelas do IP Helper
(`GetExtendedTcpTable`/`GetExtendedUdpTable`) em um *snapshot* revalidado a cada 300 ms, sem
reconsulta em caso de miss. Isso tem uma consequência que não é uma degradação suave:

* o SYN de uma conexão nova é emitido no instante do `connect()`, antes de a tabela saber dela;
* o pacote passa intacto e a conexão se estabelece **fora do túnel**, pela placa física;
* quando o snapshot atualiza, os segmentos seguintes *da mesma conexão* passam a ser
  redirecionados para o relay, que abre uma **segunda** conexão ao destino e despeja os bytes
  nela. O resultado não é um vazamento, é um stream corrompido.

**Decisão: migrar para WinDivert 2.2 e usar duas camadas ao mesmo tempo.**

| Handle | Camada | Flags | Papel |
|---|---|---|---|
| eventos | SOCKET | `SNIFF \| RECV_ONLY` | `BIND`/`CONNECT`/`ACCEPT`/`CLOSE` entregam `ProcessId` + a 5-tupla **antes do primeiro pacote existir** |
| pacotes | NETWORK | nenhuma | o redirecionamento em si |

`SNIFF` é uma escolha deliberada de segurança: sem ele, o handle SOCKET seguraria *todas* as
operações de socket da máquina até este processo consumir o evento.

O `FlowRegistry` consolida as duas fontes. O IP Helper continua sendo usado, mas só para o que
ele realmente serve: **semear** a tabela no `StartAsync` (a camada SOCKET não enxerga eventos
anteriores à abertura do handle, então conexões que o Discord já tinha ficariam invisíveis) e
como *fallback* em caso de miss. Entradas saem por `CLOSE`, não por expiração — o que elimina o
outro problema da tabela antiga, em que uma porta efêmera reciclada herdava o destino do fluxo
anterior e era discada para o servidor errado.

### 1.2 A rota do túnel — a correção central

Com split tunneling (obrigatório: sem ele o Windows manda a máquina inteira pela VPN), a
interface da VPN fica sem rota para a internet. O `VpnRouteManager` instala uma
`0.0.0.0/0` nessa interface com **métrica 9000**, via `CreateIpForwardEntry2` do IP Helper.

O truque está na assimetria da métrica. O Windows soma a métrica da interface à da rota:

| Rota | Total | Vence uma busca normal? | Vence uma busca fixada na VPN? |
|---|---|---|---|
| `0.0.0.0/0` física (metric 256, if 25) | 281 | sim | n/a |
| `0.0.0.0/0` VPN (metric 9000, if 25) | 9025 | nunca | sim, é a única candidata |

Ou seja: o tráfego normal da máquina jamais escolhe a VPN, mas um socket que já teve sua busca
restringida à interface da VPN por `IP_UNICAST_IF` encontra rota. É exatamente a assimetria que
um túnel por aplicativo precisa.

O *next hop* difere por protocolo: um link PPP (MS-SSTP) é ponto-a-ponto e aceita rota on-link
(`0.0.0.0`); um adaptador TAP (OpenVPN) é Ethernet e precisa do gateway que o servidor anunciou,
capturado pelo script `--up` do OpenVPN.

O layout dos structs de P/Invoke (`MIB_IPFORWARD_ROW2`, 104 bytes, com `DestinationPrefix` em
12, `NextHop` em 44 e `Metric` em 84) foi verificado contra a tabela de rotas real desta máquina
— o parse produziu linhas idênticas às do `Get-NetRoute` — e está fixado por teste em
`IpForwardNativeLayoutTests`.

### 1.3 Redirecionamento, não reescrita de origem

**Esta seção descreve um erro de arquitetura anterior, corrigido, que vale manter documentado.**

A versão original reescrevia o IP de origem do pacote para o IP do adaptador VPN e reinjetava
setando `WINDIVERT_ADDRESS.Network.IfIdx` para a interface da VPN, assumindo que isso faria o
pacote sair pelo túnel. **Não faz.** Quando o WinDivert reinjeta um pacote de saída, o Windows
refaz a decisão de rota a partir do **IP de destino** e ignora o `IfIdx` fornecido. O pacote
continuava saindo pela placa física, só que com o IP de origem trocado — o que quebra a conexão
em vez de tunelá-la.

O desenho atual é redirecionamento para um relay local:

1. O WinDivert desvia as conexões do app alvo para o `TcpTunnelRelay`/`UdpTunnelRelay`,
   reescrevendo **apenas o destino** para o endereço LAN da própria máquina na porta do relay, e
   **preservando a porta de origem**, que é a chave para reencontrar o destino real.
2. O pacote é reinjetado como **inbound**. Isso não é detalhe: uma injeção *outbound* segue
   descendo a pilha rumo à rede, então um pacote endereçado a um listener local nunca chega
   nele — o app fica pendurado numa conexão desviada mas não atendida.
3. O relay reabre a conexão ao destino original com um socket fixado na interface da VPN
   (`IP_UNICAST_IF` + `bind` no IP do túnel), que só funciona por causa da rota da §1.2.
4. As respostas do relay são reescritas para parecerem vir do servidor original.

O endereço LAN é usado deliberadamente em vez de `127.0.0.1`: redirecionar para loopback é um
beco sem saída documentado (basil00/WinDivert issue #82 reúne cinco tentativas fracassadas e
registra que um proxy escutando em `INADDR_ANY` é o que funciona). Esse é também o esquema da
implementação de referência mais próxima que existe, o proxy transparente do **mitmproxy** para
Windows.

### 1.3.1 Alvo é o executável, não um PID
Prender um PID único não funciona na prática: apps como o Discord rodam uma árvore de processos
(principal + renderers + helpers) e quem faz rede muitas vezes não é o PID que o usuário
escolheu; além disso todo reinício gera PIDs novos. O `ProcessGroupWatcher` acompanha o conjunto
vivo de PIDs cuja imagem corresponde ao executável selecionado, atualizando a cada ~750 ms. Isso
cobre processos filhos, sobrevive a reinícios do app e permite **armar o túnel antes de o app
existir** — conectar primeiro e abrir o Discord depois funciona.

### 1.3.2 O endereço do WinDivert é devolvido intacto
`WINDIVERT_ADDRESS` carrega mais do que direção e índice de interface: tem as flags `Loopback`,
`Impostor` e as de validade de checksum. A versão anterior montava um endereço **novo** a cada
injeção, a partir de três campos, e perdia todas as outras. Isso importa justamente aqui, porque
as respostas do relay são tráfego de loopback (origem e destino são ambos esta máquina).

O `PacketAddress` atual carrega os 80 bytes do endereço nativo opacamente e os devolve verbatim
no envio; só a direção é alterada, e só onde o motor precisa. Foi também por isso que o binding
`WinDivertSharp` foi trocado por P/Invoke próprio: precisamos de controle exato sobre esse
struct.

Ainda no mesmo espírito: o retorno de `WinDivertSend` era **descartado**. Uma injeção rejeitada
pelo driver ficava indistinguível de sucesso, com a conexão travando sem nada no log. Agora
`Send` devolve o código Win32 e o contador `InjectFailed` aparece no painel de Diagnóstico.

### 1.4 IPv6 é capturado para ser bloqueado
O filtro de captura é `outbound and (tcp or udp)`, sem `and ip` — de propósito, para pegar IPv4
**e** IPv6. Os túneis do VPN Gate são IPv4-only, então um pacote IPv6 do app alvo não tem como
ser carregado. Deixá-lo passar o mandaria direto pela placa física, vazando exatamente o tráfego
que o usuário pediu para tunelar. Capturá-lo permite **descartá-lo**, o que faz o happy-eyeballs
do app cair para IPv4 — que o túnel carrega.

### 1.4.1 Escopo de protocolo: TCP, UDP ou ambos

Nem toda aplicação quer as duas pernas tuneladas. O escopo (`TunnelProtocolScope`) é escolhido na
UI e vale para o processo alvo: **somente TCP** (padrão), **somente UDP** ou **TCP e UDP**, nessa
ordem no seletor. O padrão é o escopo mais estreito por decisão do usuário: é o que mantém DNS e
voz na rota normal da máquina enquanto só a sinalização do app vai pelo túnel.

O que fica fora do escopo **não é capturado**, em vez de ser capturado e deixado passar: o filtro do
WinDivert é montado a partir do escopo (`outbound and tcp`, `outbound and udp`, ou os dois), e o
relay correspondente sequer é iniciado — não abre socket nem conexões fixadas na VPN. O tráfego
excluído segue a rota normal da máquina, exatamente como o de qualquer outro processo. A checagem
também existe no `ProcessPacket` como defesa em profundidade, e o bloqueio de IPv6 (§1.4) passa a
respeitar o escopo: bloquear IPv6 de um transporte que o usuário deixou fora quebraria justamente o
tráfego que ele mandou seguir direto.

Consequência a assumir: com **somente TCP**, o DNS (UDP) e a voz do Discord saem fora do túnel.

### 1.5 MS-SSTP via API nativa do Windows, não reimplementado
Reimplementar SSTP/PPP/MPPE em C# é inviável. O app usa o módulo PowerShell `VpnClient`
(`Add-VpnConnection -TunnelType Sstp`) para criar a entrada, **`rasdial.exe`** para discar, e
`System.Net.NetworkInformation.NetworkInterface` (gerenciado, sem P/Invoke) para resolver
status e IP/índice do adaptador PPP. Essa combinação evita toda a superfície de marshaling de
`RASENTRY`, e é o motivo de o MS-SSTP não exigir instalação de nada.

Os parâmetros passados ao script usam **binding nomeado do PowerShell via `-File`**, nunca
concatenação em um `-Command`, então não há superfície de injeção mesmo com dados vindos do
usuário ou do VPN Gate.

**L2TP/IPsec foi removido por completo** — código, enum, script, UI, testes e documentação. Não
foi desabilitado: o feed do VPN Gate não publica a chave pré-compartilhada por servidor, então
era um protocolo que só podia falhar. Oferecer uma opção que nunca funciona é pior do que não
oferecê-la.

### 1.6 OpenVPN embarcado, sem instalação pelo usuário
OpenVPN precisa de um adaptador virtual, e todo adaptador virtual é um driver de kernel — não
existe caminho sem driver. O que dá para evitar é fazer o **usuário** lidar com isso.

Das três opções do OpenVPN 2.6:

* **wintun** exige que o `openvpn.exe` rode como **SYSTEM**, não bastando Administrador; seria
  preciso criar um serviço temporário só para elevar.
* **ovpn-dco-win** é mais novo e ainda não é o padrão nesta versão.
* **tap-windows6** funciona com privilégio de Administrador, que o app já exige pelo manifesto
  por causa do WinDivert.

**Decisão: tap-windows6.** O `openvpn.exe` 2.6.14, suas DLLs do OpenSSL, o `tapctl.exe` e o
pacote do driver (assinado pela OpenVPN Inc.) são versionados em `vendor/openvpn/` e copiados
para junto do app. Na primeira conexão OpenVPN o driver entra no *driver store* via
`pnputil /add-driver` e o adaptador é criado com `tapctl create`. Ambos idempotentes; o
adaptador é reaproveitado nas conexões seguintes.

O `OpenVpnBinaries` **nunca** procura em `C:\Program Files\OpenVPN`, no registro ou no `PATH`:
uma máquina com outra instalação do OpenVPN não pode mudar o comportamento do app.

O perfil publicado pelo servidor é usado quase verbatim, com três acréscimos obrigatórios:
`route-nopull` (senão o `redirect-gateway` do servidor sequestra a rota padrão da máquina, que é
precisamente o que este app não pode fazer), `auth-user-pass` em arquivo (não há console para o
OpenVPN pedir credenciais) e `dev-node` (para não disputar adaptador com outro cliente). O
estado da conexão vem da **interface de gerenciamento**, não de heurística sobre o log nem de
tempo decorrido.

#### 1.6.1 Duas falhas que impediam qualquer conexão OpenVPN

Ambas produziam o mesmo sintoma — nenhum servidor conectava — e nenhuma delas aparecia no log.

**(a) Barra invertida em caminho entre aspas.** Dentro de aspas o OpenVPN trata `\` como escape de
shell, então os caminhos Windows gerados (`auth-user-pass "C:\ProgramData\...\auth.txt"`) faziam
o parser abortar:

```
Options warning: Bad backslash ('') usage ... you should use double backslashes
such as "c:\openvpn\static.key"
```

O processo morria em milissegundos, **antes** de abrir a porta de gerenciamento e antes de a
diretiva `log` valer — a mensagem saía no *stderr*, que o app não lia, e nenhum arquivo de log era
criado. O app esperava 15 s e reportava "não foi possível falar com a interface de gerenciamento",
apontando para o lugar errado. Correção: `OpenVpnProfileWriter.Quote()` duplica as barras de todo
caminho entre aspas; e `OpenVpnConnection` passou a capturar stdout/stderr do cliente, de modo que
um perfil recusado agora explica o motivo em vez de virar um timeout mudo.

**(b) `hold release` engolido.** Com `management-hold` o cliente só disca depois que o app libera o
*hold*. O app enviava `state on` e `hold release` em sequência imediata; o parser do OpenVPN 2.6
processa **um comando por leitura**, então os dois chegando no mesmo segmento TCP custavam o
segundo — o log do próprio OpenVPN registrava `CMD 'state on'` duas vezes e nenhum
`SUCCESS: hold release succeeded`. O cliente ficava parado no hold até estourar o timeout de 45 s.
Como depende de temporização de rede, falhava de forma intermitente. Correção: um comando por vez,
cada um aguardando seu `SUCCESS:`/`ERROR:` antes do próximo (`SendCommandAsync`).

#### 1.6.2 Perfil `.ovpn` do usuário como fonte alternativa

O perfil não precisa vir do VPN Gate. `IOpenVpnProfileSource` carrega um `.ovpn` escolhido pelo
usuário, o que cobre qualquer provedor OpenVPN — pago, corporativo ou próprio — com o mesmo cliente
embarcado. A validação é feita no carregamento, não na discagem: sem `remote` válido, ou com
certificado apontado por arquivo externo (`ca ca.crt` em vez do bloco `<ca>`), o arquivo é recusado
na hora com uma mensagem acionável. O motivo do segundo caso é concreto: o cliente roda a partir de
uma cópia gerada em outro diretório, então um caminho relativo resolveria para a pasta errada.

O parsing do `remote`/`proto` é compartilhado (`OpenVpnRemoteParser`, na camada Application) entre
esta fonte e a do feed do VPN Gate, para que as duas concordem sobre o que um perfil aponta.

#### 1.6.3 O diretório de sessão e quem pode lê-lo

O `auth-user-pass` do OpenVPN exige as credenciais **em texto puro** em disco (§1.6). O
`OpenVpnProfileWriter` gera cada sessão em `%ProgramData%\ProxyDiscord\openvpn\<guid>\` e endurece
o ACL do diretório: herança removida, acesso só para **Administradores**, **SYSTEM** — e a
identidade do próprio processo.

Essa terceira entrada não é decoração. A primeira versão concedia apenas os dois grupos, e como a
herança some no mesmo passo, o writer se trancava para fora dos arquivos que acabara de criar: o
`File.WriteAllText` do `auth.txt` morria com `UnauthorizedAccessException` e o `Dispose` — que
engole `UnauthorizedAccessException` por contrato — falhava calado, deixando o diretório de sessão
para trás. Num processo elevado (produção) o SID do usuário já está coberto por Administradores,
então a regra extra não amplia acesso nenhum; num processo não elevado ela é o que mantém o writer
funcional em vez de vazar diretórios.

A raiz é injetável no construtor por causa disso: os testes apontam para uma pasta temporária
própria, em vez de endurecer permissões dentro do `%ProgramData%` real da máquina de quem roda a
suíte.

⚠️ **Licença:** `openvpn.exe` e `tapctl.exe` são **GPLv2**. Redistribuí-los obriga a oferecer o
código-fonte correspondente — ver `vendor/openvpn/README.md`.

### 1.7 Auto-teste de saída: a verificação que faltava
Antes de reportar "Conectado", o `VpnEgressSelfTest` pergunta a única coisa que importa: *um
socket fixado nesta interface consegue mesmo alcançar a internet?* Três checagens:

1. **TCP pelo túnel** — falha imediatamente com `NetworkUnreachable` se a rota não existir.
2. **UDP pelo túnel** (uma consulta DNS real) — TCP funcionar não prova nada sobre UDP; são
   caminhos distintos no relay, e o requisito é que ambos funcionem.
3. **IP público pelo túnel vs. IP público direto** — se forem iguais, o tráfego não está saindo
   pela VPN, por mais saudável que tudo pareça.

Falhar aqui derruba a conexão com mensagem explícita, em vez de pintar o status de verde.

### 1.8 VPN Gate: o CSV não basta
O feed (`vpngate.net/api/iphone/`) tem exatamente estas colunas: `HostName, IP, Score, Ping,
Speed, CountryLong, CountryShort, NumVpnSessions, Uptime, TotalUsers, TotalTraffic, LogType,
Operator, Message, OpenVPN_ConfigData_Base64`. **Não há nada sobre MS-SSTP.** Medido ao vivo: o
CSV traz ~95 servidores, todos com perfil OpenVPN (81 TCP, 14 UDP).

A página `vpngate.net/en/` lista ~113 servidores *com* a informação por protocolo: 96 com
OpenVPN, 80 com um bloco "SSTP Hostname", 20 com L2TP. O `VpnGateClient` busca as duas fontes e
funde por `HostName`. Falha do HTML degrada para "só OpenVPN" — nunca quebra a lista.

Isso corrigiu um bug concreto. O mapper antigo usava a porta da diretiva `remote` do perfil
OpenVPN como porta SSTP de **todos** os servidores, na teoria de que o SoftEther multiplexa os
protocolos no mesmo listener. Isso vale para os servidores TCP; nos ~14 UDP-only entregava um
número de porta UDP a um discador SSTP/TCP, produzindo exatamente o timeout `rasdial`
`-2147014836` que aparecia no log. Agora a porta MS-SSTP é fixa em 443 e só existe para quem
realmente anuncia o protocolo.

**Servidor que não oferece OpenVPN nem MS-SSTP é descartado da lista**, não listado para falhar
na discagem. Ao clicar, o app escolhe OpenVPN quando disponível e MS-SSTP caso contrário; quando
o servidor oferece os dois, o seletor de protocolo fica ativo com as duas opções.

Credenciais: todo nó VPN Gate aceita `vpn`/`vpn`, pré-preenchido mas editável.

### 1.9 WinDivert exige elevação e distribuição do driver nativo
O app declara `requireAdministrator` no `app.manifest`. Os binários do WinDivert 2.2.2 são
versionados em `vendor/windivert/x64/` e copiados por `build/VendoredNatives.props` para a
**raiz** do diretório de saída — nunca para uma subpasta `x64\`, porque o P/Invoke usa
`DllImport("WinDivert.dll")` sem caminho e o carregador do Windows não procura em subpastas.
Esse foi um bug real, e o `.props` compartilhado existe para o bloco de cópia não ser
copiado-e-colado em três `.csproj`.

## 2. Arquitetura (Clean Architecture)

```
ProxyDiscord.sln
src/
  ProxyDiscord.Domain                           (sem dependências externas)
  ProxyDiscord.Application                      -> Domain
  ProxyDiscord.Infrastructure.ProcessManagement -> Application, Domain
  ProxyDiscord.Infrastructure.VpnGate           -> Application, Domain
  ProxyDiscord.Infrastructure.Connectivity      -> Application, Domain
  ProxyDiscord.Infrastructure.Ras               -> Application, Domain   (MS-SSTP)
  ProxyDiscord.Infrastructure.OpenVpn           -> Application, Domain   (OpenVPN embarcado)
  ProxyDiscord.Infrastructure.WinDivert         -> Infrastructure.Routing (ver §2.1)
  ProxyDiscord.Infrastructure.Routing           -> Application, Domain
  ProxyDiscord.Infrastructure.StateStore        -> Application, Domain
  ProxyDiscord.Presentation.Wpf                 -> Application + todos os Infrastructure.*
tests/  (1 projeto de teste por projeto de src)
docs/DESIGN.md
```

Cada componente pedido pelo usuário tem seu próprio projeto: UI (`Presentation.Wpf`),
gerenciamento de processos (`Infrastructure.ProcessManagement`), gerenciamento de VPN
(`Infrastructure.Ras` para MS-SSTP e `Infrastructure.OpenVpn` para OpenVPN, selecionados por
`Application.Vpn.VpnConnectionRouter`), integração VPN Gate (`Infrastructure.VpnGate`), testes de
conectividade (`Infrastructure.Connectivity`), gerenciamento WinDivert
(`Infrastructure.WinDivert`), roteamento por processo (`Infrastructure.Routing`), estado/status
(`Application.Session.RoutingSessionContext` + `Infrastructure.StateStore`), e logs
(`Microsoft.Extensions.Logging` configurado no composition root — não um projeto próprio,
para não introduzir código/abstração desnecessária além do padrão `ILogger<T>` já idiomático
em .NET).

### 2.1 "Client owns the interface" entre Routing e WinDivert
`IWinDivertHandle`/`IWinDivertHandleFactory` são definidos em `Infrastructure.Routing` (o
cliente que precisa deles), e **implementados** em `Infrastructure.WinDivert`, que referencia
`Infrastructure.Routing` (não o contrário). Isso é o que permite testar toda a lógica de
roteamento (`ProcessRoutingEngineTests`) com um `FakeWinDivertHandle` em memória, sem driver
real nem privilégio de administrador — e é o motivo de `Infrastructure.WinDivert` ser o único
projeto que toca o P/Invoke do WinDivert.

Vale registrar o limite dessa estratégia, porque ele custou caro: esses testes provam as
*decisões* do motor, nunca que o tráfego anda. Toda a suíte passava enquanto nada era tunelado
(§0). É por isso que o `VpnEgressSelfTest` roda em produção, no caminho do Connect.

### 2.2 Distribuição dos binários nativos
Tudo que é nativo e de terceiros mora em `vendor/`, versionado, com procedência e licença
documentadas em `vendor/README.md` e `vendor/openvpn/README.md`:

* `vendor/windivert/x64/` — WinDivert 2.2.2-A (LGPLv3), usado por P/Invoke.
* `vendor/openvpn/` — OpenVPN 2.6.14 + tap-windows6 9.24.7 (GPLv2), executado como processo
  separado.

A cópia para o diretório de saída é feita por `build/VendoredNatives.props` (WinDivert) e por um
`ItemGroup` no `.csproj` do OpenVPN, sempre para a raiz do output — ver §1.9.

### 2.3 Fluxo Connect (`ConnectVpnUseCase`)
1. `HostEndpoint.Parse` no endereço (config manual ou linha VPN Gate selecionada — mesmo parser,
   mesmo caminho de código).
2. `RoutingSessionContext.SetConnecting()` (amarelo).
3. `IVpnConnection.ConnectAsync` → `VpnConnectionRouter` despacha para o provedor do protocolo
   (RAS/`rasdial.exe` para MS-SSTP; `openvpn.exe` embarcado para OpenVPN).
4. `GetAdapterInfoAsync` resolve IP local, índice da interface e gateway do túnel.
5. **`IVpnRouteManager.EnsureTunnelDefaultRoute`** instala a `0.0.0.0/0` de métrica alta na
   interface da VPN (§1.2). Sem esse passo nada é tunelado.
6. **`IVpnEgressSelfTest.RunAsync`** comprova que TCP e UDP realmente saem pela VPN e que o IP
   público mudou (§1.7). Falhar aqui derruba a conexão.
7. `IProcessRoutingEngine.StartAsync(target, vpnAdapter, dns)` — abre os dois handles do
   WinDivert e inicia os loops de captura.
8. Aguarda o primeiro fluxo realmente relayado (`TrafficObserved`) com timeout — não bloqueia
   indefinidamente se o processo estiver ocioso, já que o túnel fica armado.
9. Persiste o estado (`IConnectionStateStore`, para recuperação de crash — §2.5) e
   `SetConnected` (verde).

Qualquer falha em qualquer passo desfaz o que já foi feito, na ordem inversa (rota → VPN), e
marca `Error` (vermelho) com a mensagem específica.

### 2.4 Fluxo Disconnect (`DisconnectVpnUseCase`) — idempotente
Para o motor de roteamento → **remove a rota do túnel** → desconecta a VPN → limpa o estado
persistido → `SetIdle()` (preto). Cada passo tem seu próprio try/catch independente,
para uma falha não bloquear a limpeza dos passos seguintes. É chamado tanto pelo botão
Desconectar quanto pela limpeza de emergência (§2.5), e é seguro chamar mais de uma vez.

### 2.5 Limpeza em caso de crash
- `App.xaml.cs` assina `AppDomain.UnhandledException`, `AppDomain.ProcessExit`,
  `DispatcherUnhandledException` e `TaskScheduler.UnobservedTaskException`; todos chamam
  `DisconnectVpnUseCase` com um orçamento de tempo curto (5s) e um guard
  (`Interlocked.CompareExchange`) para não rodar em paralelo a partir de handlers diferentes.
- No próximo início, `CleanupStaleStateOnStartupUseCase` lê o arquivo de estado
  (`%ProgramData%\ProxyDiscord\state.json`, escrito no passo 7 do fluxo Connect). Se o PID
  dono não corresponde a uma instância viva do próprio app (`IProcessLivenessChecker`,
  comparando PID **e** horário de início para evitar falso positivo por reuso de PID), executa
  limpeza best-effort via `IVpnConnection.ForceDisconnectByNameAsync` (desconecta e remove a
  entrada RAS pelo nome, sem precisar de uma sessão `IVpnConnection` viva) e limpa o arquivo.
- O próprio driver WinDivert não precisa de limpeza explícita de estado órfão: é escopado ao
  processo que o abriu e o Windows libera automaticamente quando esse processo morre. O único
  estado do SO que realmente pode vazar entre execuções é a conexão/entrada RAS, que é
  exatamente o que este fluxo cobre.

### 2.6 Parsing de endereço compartilhado
`HostEndpoint.Parse(raw, defaultPort)` (Domain) é o único lugar do código que separa
`host:porta`. Selecionar um servidor VPN Gate na UI apenas **pré-preenche os mesmos campos**
da configuração manual (endereço, usuário, senha, PSK) em vez de ter um caminho de conexão
separado — então o parser roda uma única vez por tentativa de conexão, não duas.

### 2.7 Nome do produto e identificadores

O software se chama **Discord-VPN**. O nome aparece em quatro lugares que o usuário vê: o título da
janela, o tooltip do ícone da bandeja, o `AssemblyTitle`/`Product` (que é o que o prompt do UAC e o
Gerenciador de Tarefas mostram) e o nome da entrada de VPN criada para o MS-SSTP
(`Discord-VPN-<alvo>-<sufixo>`), que o Windows lista enquanto a conexão existe. O adaptador TAP
também passou a se chamar `Discord-VPN Tunnel`, porque aparece em "Conexões de rede".

O que **não** mudou, de propósito: `AssemblyName` e o nome do `.exe`, os namespaces e nomes de
projeto (`ProxyDiscord.*`), e os caminhos de dados — `%ProgramData%\ProxyDiscord\` para estado,
logs e perfis do OpenVPN. São identificadores, não texto de interface; renomeá-los trocaria o
caminho dos logs e do `state.json` sem nenhum ganho visível.

O único ponto que exigiu cuidado foi o adaptador TAP: ele é um objeto **permanente** do sistema.
`TapAdapterProvisioner` procura primeiro `Discord-VPN Tunnel` e depois o nome antigo
(`ProxyDiscord Tunnel`), reutilizando o que encontrar — sem isso, quem já tinha rodado a versão
anterior ganharia um segundo adaptador TAP instalado para sempre, só por causa da troca de nome.

### 2.8 Minimizar, fechar e a área de notificação
A janela é `ResizeMode="CanMinimize"`: sem redimensionamento (o layout é fixo), mas **com** o
botão de minimizar padrão do Windows na barra de título. Os dois botões fazem coisas diferentes de
propósito:

* **Minimizar** — comportamento nativo, a janela vai para a barra de tarefas. O app não intercepta
  `StateChanged`; minimizar é minimizar.
* **X** — `OnClosing` cancela o fechamento e chama `Hide()`, mandando o app para a área de
  notificação. O túnel **continua de pé**: fechar a janela não é desconectar.

A consequência disso é que o único caminho de saída passa a ser o "Fechar" do menu do ícone da
bandeja, que chama `MainWindow.AllowClose()` antes do `Shutdown()` — sem isso o `OnClosing`
cancelaria também o encerramento de verdade e o app ficaria impossível de fechar. É por isso que a
liberação é explícita em vez de depender de o WPF ignorar o cancelamento durante o shutdown.

Como o processo continua vivo com a janela escondida, a limpeza da §2.5 não é afetada: quem desfaz
VPN e rota é o `DisconnectVpnUseCase`, chamado no encerramento real ou pelos handlers de crash
(§2.9).

### 2.9 Onde a limpeza é disparada, e com quanto tempo

Toda saída passa pelo mesmo `DisconnectVpnUseCase` (§2.4) — o que muda é o gatilho e o orçamento:

| Gatilho | Orçamento | Observação |
|---|---|---|
| Botão **Desconectar** | sem limite | caminho assíncrono normal da UI |
| **Trocar o processo alvo** com sessão de pé | sem limite | `ApplyTargetAsync` desconecta antes de trocar |
| **Fechar** no menu da bandeja → `OnExit` | 15 s | cabe o desligamento gracioso do OpenVPN (8 s) e a remoção da entrada RAS |
| `SessionEnding` (logoff/desligar o Windows) | 5 s | a janela pode estar escondida; ninguém vai clicar em nada |
| Handlers de crash e `ProcessExit` | 5 s | melhor esforço antes de o processo morrer |
| App morto no Gerenciador de Tarefas | — | nada roda; a limpeza acontece no próximo início (§2.5) |

Três detalhes que não são óbvios e que já custaram caro:

* **A limpeza roda fora da thread da UI.** Ela é chamada da thread do dispatcher e bloqueia nela
  esperando a conclusão. Como as continuações do `DisconnectVpnUseCase` capturam o
  `SynchronizationContext` do dispatcher, esperar direto por ela trava as duas pontas até o
  orçamento estourar — e a limpeza simplesmente não acontece. O `Task.Run` existe para tirar a
  continuação do dispatcher.
* **O guard de concorrência não é de execução única.** Um `UnobservedTaskException` qualquer no meio
  da sessão consumiria um guard de uma vez só, e a saída de verdade sairia sem limpar nada. O que
  existe é um lock: dois handlers não rodam a limpeza ao mesmo tempo, mas ela pode rodar quantas
  vezes for preciso — é idempotente por construção (§2.4).
* **Um `Start` que falha no meio limpa o que já subiu.** `ProcessRoutingEngine.StartAsync` levanta
  watcher, relays e dois handles do WinDivert em sequência; falhar no último deixaria os primeiros
  vivos com `IsRunning == false` — invisíveis para o `StopAsync` e vazados de vez, já que a
  tentativa seguinte sobrescreveria os campos e perderia os sockets antigos.

## 3. Padrões de projeto usados

- **Clean Architecture / Ports & Adapters**: `Application/Ports` define as interfaces;
  `Infrastructure.*` implementa cada uma isoladamente; `Presentation.Wpf` é o único projeto
  que conhece todos os adaptadores concretos (composition root).
- **Client-owns-interface** entre `Infrastructure.Routing` e `Infrastructure.WinDivert`
  (§2.1) — aplicado mesmo entre dois projetos de infraestrutura, não só entre
  Application/Infrastructure.
- **Composition root único** (`CompositionRoot.cs`): cada projeto de infraestrutura expõe seu
  próprio `IServiceCollection.AddXxx()`, mantendo o cadastro de DI de cada componente junto
  do próprio componente, sem um "God file" de registro.
- **Casos de uso como orquestradores finos**: `ConnectVpnUseCase`/`DisconnectVpnUseCase`
  contêm a lógica de "o que fazer em que ordem e como desfazer"; ViewModels não têm lógica de
  negócio, só leem/assinam `RoutingSessionContext` e chamam casos de uso.
- **Estado observável compartilhado** (`RoutingSessionContext`): única fonte de verdade do
  status de conexão, latência e último erro, consumida tanto pelos casos de uso (que a
  mutam) quanto pela UI (que só a lê via `IRoutingSessionContext`, sem os métodos de
  mutação).
- **Idempotência explícita**: `DisconnectVpnUseCase` e `ForceDisconnectByNameAsync` são
  seguros para chamar repetidamente — cada etapa engole sua própria exceção e segue em
  frente, nunca deixando um recurso pela metade por causa de outro que já falhou.

## 4. Problemas encontrados e corrigidos durante a implementação

As subseções estão em ordem cronológica inversa — a rodada mais recente primeiro.

### 4.2 O da terceira rodada (o perfil OpenVPN não chegava a ser escrito)

O endurecimento do ACL do diretório de sessão do OpenVPN removia a herança e concedia acesso só a
Administradores e SYSTEM, sem incluir a identidade do processo. Consequências, nessa ordem: o
`auth.txt` não podia ser escrito, o `Write` abortava, e o `Dispose` de limpeza — que engole
`UnauthorizedAccessException` — falhava em silêncio, acumulando diretórios de sessão órfãos em
`%ProgramData%\ProxyDiscord\openvpn\`. Sob elevação o sintoma não aparece (o SID do usuário está
coberto por Administradores), então foi a suíte de testes, que roda sem elevação, que o expôs.
Corrigido concedendo também o SID do processo, e apontando os testes para uma raiz temporária
injetada em vez do `%ProgramData%` real.

Na mesma rodada, a revisão dos caminhos de saída ("ao fechar, desconectar ou trocar de aplicativo,
tudo tem que ficar limpo") encontrou mais quatro:

* **A limpeza de saída travava em si mesma.** `RunEmergencyCleanup` esperava a task do
  `DisconnectVpnUseCase` bloqueando a thread da UI, e as continuações dessa task voltavam para o
  dispatcher — deadlock até o timeout de 5 s. Ou seja: fechar o app pelo menu da bandeja
  **deixava VPN, rota e `openvpn.exe` de pé**, sem nada no log além do silêncio. Corrigido em §2.9.
* **Guard de execução única.** O mesmo método usava `Interlocked.CompareExchange` como trava
  permanente. Bastava um `UnobservedTaskException` durante a sessão para que a limpeza da saída
  fosse pulada por completo.
* **`Start` parcial vazava relay e watcher.** Sem o `TearDownAsync` no caminho de exceção, um
  `Start` que falhasse ao abrir o handle do WinDivert deixava dois sockets de relay escutando, com
  `_running == false` — o `StopAsync` seguinte retornava na primeira linha e o retry sobrescrevia
  os campos, perdendo os sockets para sempre.
* **Sessões TCP em andamento não eram fechadas na parada.** O relay UDP já derrubava cada socket de
  sessão no `DisposeAsync`; o TCP só cancelava o token e ia embora, então o desconectar seguia
  removendo rota e derrubando a VPN por baixo de conexões ainda abertas. Agora as sessões vivas são
  rastreadas, fechadas e aguardadas (3 s) antes de o `DisposeAsync` retornar.

### 4.0 Os da segunda rodada (o túnel não tunelava nada)

- **A rota que faltava (§0/§1.2)** — causa raiz. `IP_UNICAST_IF` restringe a busca de rota, não
  a cria; sem `0.0.0.0/0` na interface da VPN toda conexão do relay morria com
  `WSAENETUNREACH`. Comprovado por teste de socket direto na máquina antes de qualquer
  alteração de código.
- **Erro engolido em `LogDebug`** — o `SocketException` acima era logado em `Debug`, filtrado
  pelo nível mínimo `Information`. Falha de socket no relay virou `Warning` com o
  `SocketErrorCode`, e o nível mínimo passou para `Debug`.
- **Corrida de 300 ms na identificação do processo (§1.1)** — o SYN de toda conexão nova escapava
  do túnel e o resto do stream era relayado para uma *segunda* conexão. Resolvido por construção
  com a camada SOCKET do WinDivert 2.2.
- **`WINDIVERT_ADDRESS` remontado do zero (§1.3.2)** — as flags `Loopback`/`Impostor`/checksum
  eram descartadas em toda reinjeção, justamente num desenho em que as respostas do relay são
  tráfego de loopback.
- **Retorno de `WinDivertSend` descartado** — injeção rejeitada era indistinguível de sucesso.
- **`ReceiveFromAsync` em socket não vinculado** — o relay UDP lançava
  `InvalidOperationException` ("You must call the Bind method") em **toda** sessão UDP; aparecia
  no log como uma `UnobservedTaskException` que disparava a limpeza de emergência.
- **`Task.WhenAny` no relay TCP** — derrubava as duas pontas assim que uma direção fechava,
  truncando qualquer protocolo com half-close. Agora é `WhenAll` + `Shutdown(Send)`.
- **`Dns.GetHostAddresses` por datagrama** — resolução de nome bloqueante dentro do laço de
  recepção UDP, dezenas de vezes por segundo numa call de voz. Substituído por um snapshot dos
  endereços locais (`LocalAddressSet`).
- **`GetOrAdd` com fábrica de efeito colateral** — a fábrica podia rodar concorrentemente para a
  mesma chave e vazar o socket e a task do perdedor; e o `TryRemove` incondicional podia
  despejar uma sessão *nova e viva* que tivesse assumido a mesma chave.
- **Porta MS-SSTP derivada do perfil OpenVPN (§1.8)** — nos servidores UDP-only isso entregava
  uma porta UDP a um discador TCP, causando o timeout `rasdial -2147014836` visto no log.
- **Motor "em execução" depois de morto** — o laço de captura saía sem log e sem zerar
  `_running`, então `IsRunning` mentia para sempre.
- **Código morto** — `NatTranslationTable`/`NatSessionKey`/`NatTableEntry` não eram referenciados
  nem registrados em DI desde a mudança para relay, mas carregavam 9 testes. Removidos.

### 4.1 Os da primeira rodada

1. **Colisão de namespace `Application` vs `System.Windows.Application`** — como o projeto de
   UI (`ProxyDiscord.Presentation.Wpf`) referencia o projeto de aplicação
   (`ProxyDiscord.Application`), e ambos vivem sob o namespace raiz `ProxyDiscord`, o
   identificador nu `Application` em qualquer arquivo de `Presentation.Wpf` resolve para o
   *namespace* `ProxyDiscord.Application` (login de namespace tem prioridade sobre
   `using System.Windows;`), não para o tipo `System.Windows.Application`. Corrigido
   qualificando totalmente (`System.Windows.Application`) em `App.xaml.cs`.
2. **Checksum duplicado** — a primeira versão de `PacketRewriter` recalculava o checksum
   IPv4/TCP/UDP em C# puro (algoritmo RFC 1071 escrito à mão). Como o envio real
   (`WinDivertHandle.Send`) já precisa chamar `WinDivertHelperCalcChecksums` nativamente para
   os pacotes **não** alterados (offload de checksum de NIC), manter os dois seria código
   redundante e uma fonte extra de risco (algoritmo escrito à mão, não testável contra tráfego
   real neste ambiente). Simplificado: `PacketRewriter` apenas zera os campos de checksum
   após reescrever IP/porta; o recálculo real acontece uma única vez, no ponto de envio.
3. **P/Invoke de `GetExtendedTcpTable`/`GetExtendedUdpTable`** — a primeira versão lia as
   linhas da tabela via `Marshal.PtrToStructure` sobre um ponteiro obtido de
   `Marshal.UnsafeAddrOfPinnedArrayElement` **depois** do `GCHandle` já ter sido liberado
   (ponteiro potencialmente inválido após o GC mover o array). Corrigido lendo os campos
   diretamente do `byte[]` com `BitConverter`, sem nunca precisar re-obter um ponteiro para um
   array não fixado.
4. **Direção de referência entre `Routing` e `WinDivert`** — inicialmente `Routing` referenciava
   `WinDivert` (para abrir o handle), o que impediria `WinDivert` de implementar uma interface
   definida em `Routing` sem referência circular. Corrigido invertendo a referência (§2.1).
5. **Distribuição dos binários nativos em subpasta** — ver §1.6.
6. **P/Invoke `ref` vs `out`** — `WinDivertSharp.WinDivert.WinDivertRecv`/`WinDivertSend`
   declaram os parâmetros de tamanho (`readLen`/`sendLen`) como `ref`, não `out`, apesar de
   semanticamente serem valores de saída; corrigido inicializando as variáveis antes da
   chamada.
7. **`rasdial.exe` nunca conectava (bug crítico, encontrado após relato do usuário: "não conecta a nenhuma VPN")**
   — `RasDialRunner.DialAsync` passava `/DisableConnectedUI` como primeiro argumento antes do
   nome da entrada. Essa flag **não existe** na sintaxe real do `rasdial.exe`
   (`rasdial entryname [username [password]] [/DOMAIN:...] [/PHONE:...] [/DISCONNECT]` — ver
   `learn.microsoft.com`/`ss64.com/nt/rasdial.html`). Como os argumentos são posicionais, isso
   deslocava todos eles: `entryname` recebia o texto da flag inexistente, `username` recebia o
   nome real da entrada, `password` recebia o usuário real — a senha de verdade nunca era
   passada. Resultado: **toda tentativa de discagem falhava**, para qualquer VPN, VPN Gate ou
   manual. Corrigido removendo a flag; sintaxe agora é `rasdial entryName username password`.
8. **Porta SSTP customizada do VPN Gate era descartada (bug crítico, mesmo relato)** —
   `VpnGateEntryMapper` preferia a coluna `IP` (numérica, sem porta) sobre `HostName`
   (`vpnNNNNNNNNNN.opengw.net[:porta]`) ao montar o endereço; e mesmo quando `HostEndpoint.Parse`
   capturava corretamente a porta a partir do `HostName`, `RasVpnConnection.ConnectAsync`
   passava apenas `request.Endpoint.Host` (sem `:porta`) para `Add-VpnConnection`. VPN Gate
   documenta explicitamente (vpngate.net/en/howto_sstp.aspx) que (a) a conexão SSTP **deve**
   usar o hostname DDNS, não o IP direto, e (b) a porta SSTP de cada nó — que varia por
   servidor, ex. `vpn465380411.opengw.net:1887`, `vpn622746048.opengw.net:995`,
   `vpn115196132.opengw.net:1602` — vai embutida no mesmo campo, no formato `host:porta`,
   inclusive no diálogo nativo "Server name or address" do Windows (que usa o mesmo mecanismo
   de `Add-VpnConnection -ServerAddress`). Isso invalida a suposição anterior deste documento
   (§1.4 original) de que "SSTP é fixo em 443" — o cliente nativo do Windows aceita porta
   customizada via `host:porta`, só não expõe isso como um parâmetro separado. Corrigido: (a)
   `VpnGateEntryMapper` agora prefere `HostName` sobre `IP`; (b) `RasVpnConnection` monta o
   `ServerAddress` incluindo a porta (`request.Endpoint.ToString()`) para SSTP — extraído para
   `RasVpnConnection.BuildServerAddress`, testado isoladamente para não regredir de novo.
9. **O tráfego nunca era tunelado (bug de arquitetura, relatado pelo usuário: "continua com a
   mesma latência/conexão")** — reescrever o IP de origem e reinjetar com `IfIdx` da VPN não
   reroteia nada; o Windows refaz a rota pelo IP de destino ao reinjetar. Corrigido
   substituindo o NAT por redirecionamento a um relay local que reabre a conexão com
   `IP_UNICAST_IF` fixado na interface da VPN. Detalhes completos em §1.2. Lição registrada:
   testes contra um handle falso validam decisão e reescrita, mas **não** provam que o pacote
   sai pelo túnel — isso exige verificação em rede real (ver §5.2).
10. **Janela em branco em runtime (bug real, encontrado após relato do usuário)** — o
   scaffold original (`dotnet new wpf`) criou `MainWindow.xaml`/`MainWindow.xaml.cs` na raiz
   do projeto (`ProxyDiscord.Presentation.Wpf.MainWindow`, um `<Grid></Grid>` vazio). A UI
   real foi construída em `Views/MainWindow.xaml` (`ProxyDiscord.Presentation.Wpf.Views.
   MainWindow`), mas os arquivos antigos da raiz nunca foram apagados. Como
   `App.xaml.cs`/`CompositionRoot.cs` vivem no namespace raiz `ProxyDiscord.Presentation.Wpf`
   — o mesmo da classe antiga — e só importam `Views` via `using`, a resolução de nome do C#
   escolheu a classe do namespace atual (a antiga, vazia) em vez da importada via `using`,
   silenciosamente. Isso **compilou sem erro** (as duas classes existem e a antiga tem
   construtor sem parâmetros, então o DI conseguia construí-la), mas o DI registrava e exibia
   a janela vazia do template, nunca a UI real. Corrigido apagando os arquivos órfãos da raiz
   (`src/ProxyDiscord.Presentation.Wpf/MainWindow.xaml{,.cs}`); com a classe duplicada fora do
   caminho, `MainWindow` passa a resolver sem ambiguidade para `Views.MainWindow`. Ficou
   registrado aqui como lembrete: ao mover/renomear uma janela WPF gerada por scaffold para
   uma subpasta, sempre apagar o arquivo original — uma classe duplicada no namespace raiz do
   projeto pode ser escolhida silenciosamente no lugar da pretendida, sem erro de compilação.

## 5. Testes

### 5.1 Automatizados (rodam em `dotnet test`, sem admin/rede)

| Área | O que é coberto |
|---|---|
| `HostEndpoint.Parse` | host com/sem porta, porta inválida, string vazia |
| **Filtros do WinDivert** | o filtro de **cada escopo** (TCP+UDP, só TCP, só UDP) e o `SocketEventFilter` compilados pelo **parser real** do WinDivert 2.2; `processId` aceito na camada SOCKET e rejeitado na NETWORK — as duas afirmações em que todo o desenho de dois handles se apoia. Não exige admin: compilar um filtro não carrega o driver |
| **Layout dos structs de rota** | `MIB_IPFORWARD_ROW2` com 104 bytes e offsets 12/44/84, mais um parse da tabela de rotas **real** da máquina. Um offset errado aqui não lança exceção, entrega um registro malformado ao kernel |
| **`FlowRegistry`** | semeadura pelo IP Helper (conexões anteriores à abertura do handle), `CONNECT` registrando dono + destino, `CLOSE` removendo, `BIND` não apagando um destino já conhecido, destino vindo do pacote para UDP não conectado, socket IPv6 marcado, fallback em caso de miss, expiração de backstop |
| **Roteamento por processo (`ProcessRoutingEngine`)** | redirecionamento ao relay preservando a porta de origem; destino real registrado; **conexão anunciada só pela camada SOCKET redirecionada desde o primeiro pacote**; `CLOSE` removendo o fluxo; pacote de outro processo passa intacto; tráfego misto; resposta do relay restaurada; **IPv6 do alvo descartado em vez de vazar**; **injeção rejeitada contabilizada**; `TrafficObserved` só quando algo é de fato relayado; `StopAsync` desbloqueia o laço |
| Reescrita de pacote (`PacketRewriter`) | parsing de campos IPv4/TCP, flags FIN/RST, SNAT/DNAT, zeragem de checksum, pacote inválido/curto |
| **VPN Gate** | endpoint OpenVPN vindo do perfil publicado, TCP vs UDP, **MS-SSTP só quando o servidor anuncia**, **porta SSTP sempre 443 e nunca derivada do perfil OpenVPN**, servidor sem nenhum protocolo suportado descartado, servidor só-SSTP mantido, ambos os protocolos → OpenVPN preferido; scraper do HTML (com e sem SSTP, markup irreconhecível degradando para vazio); leitura do perfil (host/porta/proto, CRLF, linhas `#remote` comentadas, porta fora de faixa, fixture do feed real) |
| Ping paralelo (`ParallelProbeRunner`) | N testes concorrentes, um teste lento não atrasa os demais, timeout de item sem cancelar o lote, streaming incremental, lista vazia |
| Casos de uso | caminho feliz, rollback em cada ponto de falha, idempotência do disconnect, ordem de chamadas, detecção de estado órfão vs. instância viva |
| Descoberta de processos / liveness | ordenação, filtragem, PID reciclado com horário diferente |
| Estado persistido | round-trip, limpeza, arquivo corrompido não derruba a leitura |
| ViewModels | status→cor, gating de `CanConnect`/`CanDisconnect`, **OpenVPN recusado sem perfil**, **seletor de protocolo limitado ao que o servidor oferece**, **troca de protocolo re-aponta o endpoint**, pré-preenchimento ao selecionar servidor, **perfil `.ovpn` preenchendo os campos e substituindo o servidor da lista**, **escopo repassado ao motor de roteamento** |
| **Seletor de processos** | agrupamento estilo Gerenciador de Tarefas: instâncias do mesmo executável em um grupo, **binários homônimos em caminhos diferentes não são fundidos**, processos sem caminho legível agrupados por nome, ordenação, seleção do grupo resolvendo para uma instância |
| **Perfil OpenVPN gerado** | **barras invertidas escapadas em todo caminho entre aspas** (regressão da falha §1.6.1a), `route-nopull`/`management-hold`/`dev-node` presentes, credenciais no arquivo apontado, perfil sem `remote` recusado, diretório da sessão removido no dispose, **ACL do diretório protegido (sem herança) concedendo Administradores, SYSTEM e o próprio processo — que continua podendo escrever ali** (§1.6.3) |
| **Perfil `.ovpn` do usuário** | endpoint e transporte lidos do arquivo, `proto` ausente → TCP, `#remote` comentado recusado, certificado por arquivo externo recusado, bloco inline aceito, arquivo inexistente recusado |
| **Escopo do túnel** | filtro de captura por escopo, TCP tunelado com UDP intacto e vice-versa, relay do transporte excluído não iniciado, escopo refletido no diagnóstico |
| **Limpeza do motor** | **`Start` que falha no meio não deixa relay escutando** (porta sondada de verdade), watcher liberado, e o `Start` seguinte funciona com porta nova |

Total: **211 testes**, todos passando (`dotnet test --filter "Category!=RequiresAdmin"`).

### 5.2 O que os testes automatizados NÃO provam

Isto merece destaque, porque foi a lição cara desta rodada: **a suíte inteira passava enquanto
nenhum byte era tunelado.** Os testes do motor exercitam decisões contra um handle falso; nenhum
deles podia detectar que a interface da VPN não tinha rota. Não era um teste quebrado, era um
limite real da estratégia.

Duas coisas foram feitas a respeito:

1. **`VpnEgressSelfTest` roda em produção**, no caminho do Connect, e derruba a conexão se o
   tráfego não estiver realmente saindo pela VPN (§1.7).
2. **Painel de Diagnóstico** com o caminho do pacote em ordem — a primeira linha vermelha é o
   estágio quebrado — e contadores separados por TCP e UDP.

### 5.3 Verificação manual obrigatória

`ProxyDiscord.Integration.Tests` (`[Trait("Category","RequiresAdmin")]`, fora do `dotnet test`
padrão) documenta cada cenário com `Skip` e o procedimento exato:

- driver WinDivert real: abrir/fechar handles NETWORK e SOCKET;
- discagem real MS-SSTP e OpenVPN (incluindo a instalação do TAP na primeira execução e um
  servidor `proto udp`);
- rota do túnel presente na interface da VPN **sem** roubar a rota padrão da máquina;
- TCP real do Discord tunelado enquanto o navegador continua saindo pelo IP normal;
- **UDP real (call de voz)** — TCP funcionar não prova nada sobre UDP;
- limpeza após matar o app no meio de uma sessão (VPN, rota e processo OpenVPN);
- **limpeza pelo menu da bandeja com tráfego real em andamento**: nenhuma conexão do relay pela VPN
  sobra, `openvpn.exe` some e a entrada RAS é removida — é o que o drenar de sessões TCP e o
  orçamento de 15 s da §2.9 sustentam, e nenhum teste automatizado alcança.

**Estado desta entrega:** compila, 211 testes passando, e os filtros do WinDivert 2.2 e o layout
dos structs de rota foram validados contra as APIs nativas reais desta máquina. O que **não** foi
possível executar aqui é a discagem real e a instalação do driver TAP, ambas exigindo elevação —
é o que a lista acima cobre.
