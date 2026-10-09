$ErrorActionPreference = 'Stop'

$installerUrl = 'https://github.com/wiresock/ndisapi/releases/download/v3.6.2/Windows.Packet.Filter.3.6.2.1.x64.msi'
$installerSize = 819200L
$installerSha256 = '9c388c0b7f189f7fa98720bae2caecf7d64f30910838b80b438ecf8956b8502c'
$minimumDriverVersion = [version]'3.6.1.0'
$maximumDriverVersion = [version]'4.0.0.0'

function Get-SystemDirectory {
    if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
        return Join-Path $env:windir 'Sysnative'
    }

    Join-Path $env:windir 'System32'
}

function Test-WindowsPacketFilterCompatible {
    $service = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\NDISRD' -ErrorAction SilentlyContinue
    $driverPath = Join-Path (Get-SystemDirectory) 'drivers\ndisrd.sys'

    if ($null -eq $service -or -not (Test-Path -LiteralPath $driverPath)) {
        return $false
    }

    $driverVersion = [version](Get-Item -LiteralPath $driverPath).VersionInfo.FileVersion
    return $driverVersion -ge $minimumDriverVersion -and $driverVersion -lt $maximumDriverVersion
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'Este instalador oferece suporte somente ao Windows x64.'
}

if (Test-WindowsPacketFilterCompatible) {
    $installedVersion = (Get-Item -LiteralPath (Join-Path (Get-SystemDirectory) 'drivers\ndisrd.sys')).VersionInfo.FileVersion
    Write-Host "Windows Packet Filter compatível já instalado (versão $installedVersion). Nenhuma alteração foi feita."
    exit 0
}

if (-not (Test-IsAdministrator)) {
    $powerShellPath = if ([Environment]::Is64BitProcess) {
        (Get-Process -Id $PID).Path
    }
    else {
        Join-Path $env:windir 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'
    }

    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $elevatedProcess = Start-Process -FilePath $powerShellPath -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    exit $elevatedProcess.ExitCode
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$temporaryDirectory = Join-Path $env:TEMP "Discord-VPN-WPF-$([guid]::NewGuid().ToString('N'))"
$installerPath = Join-Path $temporaryDirectory 'Windows.Packet.Filter.3.6.2.1.x64.msi'
$logPath = Join-Path $env:TEMP "Discord-VPN-WPF-install-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"

New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null

try {
    Write-Host 'Baixando o Windows Packet Filter x64 da origem oficial...'
    Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath -UseBasicParsing

    $downloadedSize = (Get-Item -LiteralPath $installerPath).Length
    if ($downloadedSize -ne $installerSize) {
        throw "Tamanho inesperado do MSI: $downloadedSize bytes. A instalação foi cancelada."
    }

    $downloadedHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($downloadedHash -ne $installerSha256) {
        throw 'O SHA-256 do MSI não corresponde ao valor oficial. A instalação foi cancelada.'
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $installerPath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Assinatura Authenticode inválida ($($signature.Status)). A instalação foi cancelada."
    }

    Write-Host "Pacote validado; assinatura: $($signature.SignerCertificate.Subject)"
    Write-Host 'Instalando o driver de rede. O Windows pode pedir confirmação de administrador...'

    $msiexecPath = Join-Path (Get-SystemDirectory) 'msiexec.exe'
    $arguments = "/i `"$installerPath`" /passive /norestart /L*v `"$logPath`""
    $process = Start-Process -FilePath $msiexecPath -ArgumentList $arguments -Wait -PassThru

    if ($process.ExitCode -notin @(0, 3010)) {
        throw "O Windows Installer retornou o código $($process.ExitCode). Consulte: $logPath"
    }

    if (-not (Test-WindowsPacketFilterCompatible)) {
        throw "A instalação terminou, mas o serviço NDISRD e o driver compatível não foram confirmados. Consulte: $logPath"
    }

    $installedVersion = (Get-Item -LiteralPath (Join-Path (Get-SystemDirectory) 'drivers\ndisrd.sys')).VersionInfo.FileVersion
    Write-Host "Windows Packet Filter $installedVersion instalado e compatível com ProxiFyre."
    Write-Host "Log da instalação: $logPath"

    if ($process.ExitCode -eq 3010) {
        Write-Warning 'O Windows informou que é necessário reiniciar o computador antes de usar o driver.'
    }
}
finally {
    Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
