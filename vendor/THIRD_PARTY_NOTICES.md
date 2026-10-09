# Avisos de componentes de terceiros

O Discord-VPN distribui os componentes abaixo em pastas próprias. Cada componente mantém sua licença original; este documento não altera os termos do aplicativo.

| Componente | Versão detectada | Licença/aviso no pacote | Fonte incluída em `licenses/vendor/.../source` |
|---|---:|---|---|
| OpenVPN | 2.6.14 | `openvpn/LICENSE.txt` e `openvpn/licenses/OpenVPN-COPYING-2.6.14.txt` — GPL-2.0 com exceções indicadas pelo projeto | `openvpn-v2.6.14-source.zip` |
| LZO | 2.10 | `openvpn/licenses/LZO-COPYING.txt` — GPL, com exceção de vinculação descrita no aviso do OpenVPN | `lzo-2.10-source.tar.gz` |
| pkcs11-helper | 1.30.0 | `openvpn/licenses/pkcs11-helper-BSD.txt` — a biblioteca declara opção BSD ou GPL | `pkcs11-helper-1.30.0-source.zip` |
| OpenSSL | 3.4.1 | `openvpn/licenses/OpenSSL-Apache-2.0.txt`; o `openvpn/LICENSE.txt` legado também é mantido | Fonte não incluída; licença Apache-2.0 não exige distribuição do código-fonte |
| TAP-Windows | 9.24.7.601 | Aviso GPL-2.0 em `openvpn/LICENSE.txt` | **Pendente:** não foi localizado código-fonte oficial que corresponda comprovadamente a este binário. O arquivo oficial `tap-windows-9.24.7.zip` contém binários, não fontes; por isso não é distribuído como fonte aqui. |
| WinDivert | 2.2.2 | `windivert/LICENSE.txt` — escolha entre LGPL-3.0 e GPL-2.0 | `Divert-v2.2.2-source.zip` |
| ProxiFyre | 2.6.2 | `proxifyre/licenses/AGPL-3.0.txt` | `ProxiFyre-v2.6.2-source.zip` |
| Newtonsoft.Json | 13.0.3 | `proxifyre/licenses/Newtonsoft.Json-MIT.txt` | Incluído no código-fonte do ProxiFyre |
| NLog | 5.2.3 | `proxifyre/licenses/NLog-BSD-3-Clause.txt` | Incluído no código-fonte do ProxiFyre |
| Topshelf | 4.3.0 | `proxifyre/licenses/Topshelf-Apache-2.0.txt` | Incluído no código-fonte do ProxiFyre |

## Fontes e integridade

Os arquivos listados abaixo são arquivos-fonte oficiais, sem alterações. SHA-256:

| Arquivo | SHA-256 |
|---|---|
| `openvpn/source/openvpn-v2.6.14-source.zip` | `D8A9BB2A8596B6DD4ECD311AF3B25D8DECFBC05F418FFAB2BDA0DF3DAAFEB6C6` |
| `openvpn/source/lzo-2.10-source.tar.gz` | `C0F892943208266F9B6543B3AE308FAB6284C5C90E627931446FB49B4221A072` |
| `openvpn/source/pkcs11-helper-1.30.0-source.zip` | `3FD44261DA1CED437F79137C91845FAF4F4483B5AAE3E50585D4B38AEDD8C455` |
| `windivert/source/Divert-v2.2.2-source.zip` | `65EC79C9E6AFA99F648A3F4D1F6DB794640B40D0B65BD438770EA503EE14ECB7` |
| `proxifyre/source/ProxiFyre-v2.6.2-source.zip` | `F94A2D6218DCA809C9AF39D2BF15F94762FFC0D4DE7E7BB08A406701EDCA1FEA` |

## Componentes externos e observações de distribuição

- O script `Instalar-WindowsPacketFilter.ps1` baixa o MSI do Windows Packet Filter durante a instalação; esse MSI não está incluído no pacote do Discord-VPN. Os termos de distribuição do produto Windows Packet Filter são separados e não são concedidos pelo ProxiFyre.
- `openvpn/bin/vcruntime140.dll` é um runtime da Microsoft. A Microsoft limita a redistribuição dos runtimes do Visual C++ aos arquivos e condições permitidos pela licença aplicável do Visual Studio. Confirme essa autorização para a origem usada antes de publicar uma release.
- O pacote portátil self-contained também contém o runtime .NET e dependências Microsoft. Os avisos/licenças gerados pelo `dotnet publish` devem ser mantidos no ZIP.
- A distribuição do TAP-Windows 9.24.7.601 ainda não tem fonte correspondente demonstrada neste repositório. A licença em si está registrada, mas este item impede afirmar que o pacote contém todo o material de fonte correspondente exigido para cada binário GPL.
- A presença destes avisos e fontes melhora a documentação da distribuição, mas não é uma certificação jurídica. Em particular, resolva as pendências do TAP e do runtime Microsoft antes de tratar a release como liberada para redistribuição.

## Origem oficial

- OpenVPN: <https://github.com/OpenVPN/openvpn/tree/v2.6.14>
- LZO: <https://www.oberhumer.com/opensource/lzo/download/>
- pkcs11-helper: <https://github.com/OpenSC/pkcs11-helper/tree/pkcs11-helper-1.30.0>
- WinDivert: <https://github.com/basil00/Divert/tree/v2.2.2>
- ProxiFyre: <https://github.com/wiresock/proxifyre/tree/v2.6.2>
- OpenSSL 3.4.1 license: <https://github.com/openssl/openssl/blob/openssl-3.4.1/LICENSE.txt>
- Visual C++ redistribution terms: <https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files>
- Windows Packet Filter: <https://github.com/wiresock/ndisapi>
