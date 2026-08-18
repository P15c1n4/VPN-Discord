# OpenVPN 2.6.14 + tap-windows6 9.24.7 (amd64)

Binários oficiais, redistribuídos para que o usuário final **não precise instalar o OpenVPN**.
Nada aqui é compilado a partir deste repositório.

## Conteúdo

| Caminho | Origem | Papel |
|---|---|---|
| `bin/openvpn.exe` | `OpenVPN-2.6.14-I001-amd64.msi` (swupdate.openvpn.net) | cliente OpenVPN, executado como processo filho |
| `bin/tapctl.exe` | idem | cria/lista adaptadores TAP |
| `bin/libssl-3-x64.dll`, `bin/libcrypto-3-x64.dll` | idem | OpenSSL 3.4.1, exigido pelo `openvpn.exe` |
| `bin/libpkcs11-helper-1.dll`, `bin/vcruntime140.dll` | idem | demais dependências de runtime |
| `driver/OemVista.inf`, `driver/tap0901.cat`, `driver/tap0901.sys` | `tap-windows-9.24.7.zip`, pasta `amd64/win10/` (build.openvpn.net) | driver do adaptador virtual, assinado pela OpenVPN Inc. |

Verificado: `bin/openvpn.exe --version` roda a partir desta pasta sem nenhuma instalação
(`OpenVPN 2.6.14 ... [DCO] built on Apr 2 2025`), ou seja, o conjunto de DLLs está completo.

## Por que TAP e não Wintun ou DCO

O OpenVPN precisa de um adaptador virtual, e todo adaptador virtual é um driver de kernel — não
existe caminho sem driver. Das três opções do OpenVPN 2.6:

- **wintun** exige que o `openvpn.exe` rode como **SYSTEM**, não bastando Administrador; seria
  preciso criar um serviço temporário só para elevar.
- **ovpn-dco-win** é mais novo e ainda não é o padrão nesta versão.
- **tap-windows6** funciona com privilégio de Administrador, que o app já exige pelo manifesto.

O driver é instalado silenciosamente na primeira conexão OpenVPN (`pnputil /add-driver`), e o
adaptador é criado com `tapctl.exe create`. Nada é pedido ao usuário.

## Licença

`openvpn.exe` e `tapctl.exe` são **GPLv2** (ver `LICENSE.txt`). Redistribuí-los obriga a oferecer o
código-fonte correspondente. O código-fonte exato desta versão está em
<https://github.com/OpenVPN/openvpn/releases/tag/v2.6.14> e o do driver em
<https://github.com/OpenVPN/tap-windows6>.

O app invoca o `openvpn.exe` como processo separado, sem vinculá-lo ao próprio binário — é a mesma
relação que qualquer front-end de OpenVPN tem com o cliente.

## Como atualizar

1. Baixar `OpenVPN-<versão>-I001-amd64.msi` e extrair com
   `msiexec /a OpenVPN-...msi /qn TARGETDIR=<pasta>`.
2. Copiar de `<pasta>\OpenVPN\bin` os arquivos listados na tabela acima.
3. Baixar `tap-windows-<versão>.zip` e copiar `amd64/win10/`.
4. Rodar `bin/openvpn.exe --version` a partir da pasta para confirmar que nenhuma DLL ficou faltando.
