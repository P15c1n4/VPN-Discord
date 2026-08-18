# Binários de terceiros redistribuídos

Nada aqui é compilado a partir deste repositório. São binários oficiais, versionados junto com o
app para que o usuário final não precise instalar nada manualmente.

## `windivert/` — WinDivert 2.2.2-A

- Origem: <https://reqrypt.org/windivert.html> (`WinDivert-2.2.2-A.zip`, pasta `x64/`).
- Arquivos: `WinDivert.dll` (biblioteca em modo usuário) e `WinDivert64.sys` (driver de kernel,
  assinado pelos mantenedores do WinDivert; carregado sob demanda pela DLL, exige elevação).
- Licença: LGPL v3 — ver `windivert/LICENSE.txt`. O app usa a DLL apenas por P/Invoke, sem
  vinculação estática, o que mantém a obrigação restrita a redistribuir a licença e permitir a
  substituição da DLL.
- Por que a versão 2.2 e não a 1.4: só a partir da 2.x existe a camada SOCKET, que informa o
  `ProcessId` no momento do `connect()`. Sem ela não há como associar um pacote ao processo dono
  sem uma corrida contra as tabelas do IP Helper.

## `openvpn/` — OpenVPN 2.6 + driver tap-windows6

Ver `openvpn/README.md`.
