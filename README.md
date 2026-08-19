# Discord-VPN

O Discord-VPN é uma ferramenta desenvolvida para facilitar o uso de sistemas públicos de VPN da VPN Gate, pertencente a Graduate School of University of Tsukuba, no Japão, cujo lema é: **"Free Access to World Knowledge Beyond Government's Firewall."** Além disso, oferece suporte a outros sistemas privados de VPN por meio dos protocolos OpenVPN e MS-SSTP.

O Discord-VPN limita-se a realizar o tunelamento de pacotes TCP e UDP **APENAS** no processo escolhido do sistema operacional, sem afetar outros programas ou processos em execução na máquina.

---
## **ATENÇÃO**
>### WinDivert 2.2
>WinDivert pode apresentar incompatibilidade com outros softwares que realizem leitura, redirecionamento, bloqueio, filtragem ou manipulação de pacotes, como medidores de DPS em jogos, sistemas de monitoramento de tráfego ou outros proxies e redutores de ping.
>### .NET 8
> Instalação Desnecessária na versão FULL_PORTABLE.
>### Contem Claude.

---
## USO

### Interface

<img width="885" height="684" alt="image" src="https://github.com/user-attachments/assets/b0e8e5dc-0231-492e-a625-9437443f2d4d" />

## Conectando à VPN

Ao abrir o programa com o aplicativo **Discord** já iniciado, por exemplo, ele será reconhecido imediatamente. Caso não apareça, é possível clicar em **"Processo"** e escolher manualmente o aplicativo que deseja tunelar.

<img width="882" height="678" alt="image" src="https://github.com/user-attachments/assets/6a35dccd-8f6f-406a-941e-6a45d7701bec" />

## Selecionando o tipo de protocolo a ser tunelado

Na interface, é possível selecionar quais protocolos serão tunelados: **TCP**, **UDP** ou **ambos**.

No exemplo do **Discord**, a API de transmissão de lives (**Go Live**) verifica o país de origem por meio de tráfego **TCP**. Já a transmissão de vídeo e voz é realizada pelo protocolo **UDP**.

Isso significa que, utilizando apenas **TCP**, podemos alterar a região utilizada na verificação de restrições sem afetar a latência e a qualidade da transmissão de vídeo e voz.

<img width="881" height="686" alt="image" src="https://github.com/user-attachments/assets/0ad9cb26-4c63-409b-8b34-1ee195a0eeda" />

## Selecionando um servidor VPN

Ao atualizar a lista de servidores públicos de VPN da **VPN Gate**, podemos utilizar qualquer um dos servidores disponíveis.

Minha recomendação são os servidores denominados **"public-vpn-XXX"**, pois pertencem à própria University of Tsukuba, no Japão. Neste exemplo do Discord, o menor ping não é muito importante, pois a VPN será utilizada apenas para a verificação feita por TCP.

Ao clicar duas vezes sobre um servidor, serão exibidos os dados de conexão e o botão para conectar será habilitado.

<img width="886" height="684" alt="image" src="https://github.com/user-attachments/assets/8550b820-36ff-45a0-968a-317e00247633" />

## Conectando

Ao clicar em **"Conectar"**, é possível visualizar o status da conexão no canto superior direito.

Em caso de falha, vale a pena tentar novamente, pois, como se trata de servidores públicos, é possível encontrar alguma instabilidade ou inconsistência.

Quando a janela de permissão do Windows aparecer, é **necessário permitir** para que a conexão seja estabelecida.

<img width="885" height="685" alt="image" src="https://github.com/user-attachments/assets/c93c4cc2-14c8-4843-a711-67d2a4c29eab" />

## Conectado

Quando o status mudar para **Conectado** (verde), a VPN estará conectada e o processo do Discord estará sendo tunelado pela VPN.

**PRESSIONE `CTRL + R` NO APLICATIVO DISCORD PARA REINICIÁ-LO.**

Como neste exemplo apenas o **TCP** foi selecionado, a voz e o vídeo não serão afetados pela maior latência do servidor VPN.

<img width="887" height="684" alt="image" src="https://github.com/user-attachments/assets/6bfda8fe-a7a5-44cc-a77d-1f4e3f2a4081" />

## Resultado

<img width="315" height="134" alt="image" src="https://github.com/user-attachments/assets/82b19dae-a411-4fc2-b62f-91584562cc3b" />


