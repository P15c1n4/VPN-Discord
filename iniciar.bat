@echo off
chcp 65001 >nul
setlocal

rem ---------------------------------------------------------------------------
rem  Discord-VPN - inicio rapido
rem
rem  Uso:
rem    iniciar.bat            compila e abre o app
rem    iniciar.bat nobuild    so abre o que ja esta compilado (mais rapido)
rem ---------------------------------------------------------------------------

rem O app declara requireAdministrator no manifesto: o driver do WinDivert e a
rem rota do tunel precisam de elevacao. Elevar aqui, e nao no exe, evita um
rem segundo prompt de UAC e deixa o build tambem rodar elevado - o que importa
rem porque o driver antigo so pode ser removido com privilegio.
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Solicitando privilegios de administrador...
    if "%~1"=="" (
        powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    ) else (
        powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -ArgumentList '%*'"
    )
    exit /b
)

cd /d "%~dp0"

set "EXE=src\ProxyDiscord.Presentation.Wpf\bin\Release\net8.0-windows\ProxyDiscord.Presentation.Wpf.exe"

rem O app migrou para o WinDivert 2.2. Se o servico do 1.4 de uma execucao
rem antiga ainda estiver carregado, ele segura o WinDivert64.sys dentro de bin\
rem e o build falha com "arquivo em uso por outro processo".
sc query WinDivert1.4 >nul 2>&1
if %errorlevel% equ 0 (
    echo Removendo o driver WinDivert 1.4 de execucoes anteriores...
    sc stop WinDivert1.4 >nul 2>&1
    sc delete WinDivert1.4 >nul 2>&1
)

if /i "%~1"=="nobuild" goto :run

where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo ERRO: o .NET SDK nao foi encontrado no PATH.
    echo Instale o .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

echo Compilando...
dotnet build "src\ProxyDiscord.Presentation.Wpf\ProxyDiscord.Presentation.Wpf.csproj" -c Release --nologo -v minimal
if %errorlevel% neq 0 (
    echo.
    echo A compilacao falhou. Nada foi iniciado.
    echo.
    pause
    exit /b 1
)

:run
if not exist "%EXE%" (
    echo.
    echo ERRO: executavel nao encontrado em:
    echo   %EXE%
    echo Rode "iniciar.bat" sem o parametro nobuild para compilar primeiro.
    echo.
    pause
    exit /b 1
)

echo Iniciando o Discord-VPN...
start "" "%EXE%"
exit /b 0
