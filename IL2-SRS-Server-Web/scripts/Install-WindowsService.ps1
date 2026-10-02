<#
.SYNOPSIS
    Installs the IL2-SRS web server as a Windows service.

.DESCRIPTION
    Creates (or updates) a Windows service that runs IL2-SRS-Server.exe with its own data folder,
    writes srs-server.json with the admin password and web address if it does not exist yet,
    adds firewall rules for the SRS port (TCP and UDP) and starts the service.
    Run from an elevated PowerShell prompt.

.EXAMPLE
    .\Install-WindowsService.ps1 -DataDirectory C:\IL2-SRS\main -AdminPassword 'a-long-password'

.EXAMPLE
    # A second instance on another port
    .\Install-WindowsService.ps1 -ServiceName IL2-SRS-Server-2 -DataDirectory C:\IL2-SRS\second -SrsPort 6003 -WebUrl http://localhost:8081
#>
[CmdletBinding()]
param(
    [string] $ServiceName = 'IL2-SRS-Server',
    [Parameter(Mandatory = $true)] [string] $DataDirectory,
    [string] $ExecutablePath = (Join-Path $PSScriptRoot 'IL2-SRS-Server.exe'),
    [string] $AdminPassword,
    [int] $SrsPort = 6002,
    [string] $WebUrl = 'http://localhost:8080',
    [switch] $NoFirewallRules
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell prompt.'
}

$ExecutablePath = (Resolve-Path $ExecutablePath).Path
New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
$DataDirectory = (Resolve-Path $DataDirectory).Path

# Service accounts do not see your shell's environment variables, so settings go in srs-server.json.
$configFile = Join-Path $DataDirectory 'srs-server.json'
if (-not (Test-Path $configFile)) {
    if (-not $AdminPassword) {
        $secure = Read-Host -AsSecureString 'Admin password for the web UI (at least 8 characters)'
        $AdminPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    }

    if ($AdminPassword.Length -lt 8) {
        throw 'The admin password must be at least 8 characters long.'
    }

    [ordered]@{
        SRS_ADMIN_PASSWORD = $AdminPassword
        SRS_SERVER_PORT    = "$SrsPort"
        urls               = $WebUrl
    } | ConvertTo-Json | Set-Content -Path $configFile -Encoding UTF8

    # Only Administrators and SYSTEM may read the file holding the password.
    icacls $configFile /inheritance:r /grant:r '*S-1-5-32-544:F' '*S-1-5-18:F' | Out-Null
    Write-Host "Wrote $configFile"
}
else {
    Write-Host "Keeping existing $configFile"
}

$binaryPath = "`"$ExecutablePath`" --data-dir `"$DataDirectory`""
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName
    }

    sc.exe config $ServiceName binPath= $binaryPath start= auto | Out-Null
    Write-Host "Updated service $ServiceName"
}
else {
    New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName "IL2-SRS Server ($ServiceName)" `
        -Description 'IL2 SimpleRadio Standalone server with web admin UI and API.' -StartupType Automatic | Out-Null
    Write-Host "Created service $ServiceName"
}

# Restart automatically after a crash: after 10 s, 30 s, then every 60 s.
sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null

if (-not $NoFirewallRules) {
    foreach ($protocol in 'TCP', 'UDP') {
        $ruleName = "$ServiceName $protocol $SrsPort"
        if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol $protocol `
                -LocalPort $SrsPort -Program $ExecutablePath | Out-Null
            Write-Host "Added firewall rule $ruleName"
        }
    }
}

Start-Service -Name $ServiceName
Write-Host "Started $ServiceName. Admin UI: $WebUrl  Logs: $(Join-Path $DataDirectory 'serverlog.txt')"
