# LibreGuard VPN Service - Install / Uninstall Script
# Run this script once as Administrator to register the Windows service.
# The script stages service binaries into ProgramData so LocalSystem can start them reliably.

param(
    [Parameter(Position = 0)]
    [ValidateSet("install", "uninstall", "status")]
    [string]$Action = "install",

    [string]$ServiceExePath
)

$ErrorActionPreference = "Stop"
$ServiceName = "LibreGuardVpnService"
$DisplayName = "LibreGuard VPN Service"
$Description = "Handles privileged VPN operations (certificate import, VPN entry management, IPsec configuration) for the LibreGuard VPN Desktop app. Runs as LocalSystem so the desktop app requires no UAC prompts."
$ServiceInstallDir = Join-Path $env:ProgramData "LibreGuard VPN\Service"

function Copy-ServicePayload {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceExePath
    )

    $resolvedExePath = (Resolve-Path $SourceExePath).Path
    $sourceDir = Split-Path -Parent $resolvedExePath

    New-Item -ItemType Directory -Force -Path $ServiceInstallDir | Out-Null

    Write-Host "Staging service payload to '$ServiceInstallDir'..."
    robocopy $sourceDir $ServiceInstallDir /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -gt 7) {
        throw "Failed to copy service payload to '$ServiceInstallDir' (robocopy exit code $LASTEXITCODE)."
    }

    return (Join-Path $ServiceInstallDir "LibreGuard.VpnService.exe")
}

function Install-Service {
    if (-not $ServiceExePath) {
        $scriptDir = Split-Path -Parent $MyInvocation.ScriptName
        $ServiceExePath = Join-Path $scriptDir "LibreGuard.VpnService.exe"
    }

    if (-not (Test-Path $ServiceExePath)) {
        Write-Error "Service executable not found at: $ServiceExePath"
        Write-Host "Build the service first:"
        Write-Host "  dotnet publish LibreGuard.VpnService -c Release -o .\publish"
        return
    }

    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "Service '$ServiceName' already exists. Stopping and removing..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 2
    }

    $stagedServiceExePath = Copy-ServicePayload -SourceExePath $ServiceExePath

    Write-Host "Installing service..."
    sc.exe create $ServiceName `
        binPath= "`"$stagedServiceExePath`"" `
        start= auto `
        DisplayName= "`"$DisplayName`"" | Out-Null

    sc.exe description $ServiceName "`"$Description`"" | Out-Null
    sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

    Write-Host "Starting service..."
    Start-Service -Name $ServiceName

    $svc = Get-Service -Name $ServiceName
    Write-Host "Service '$ServiceName' installed from '$stagedServiceExePath' and $($svc.Status)."
}

function Uninstall-Service {
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $existing) {
        Write-Host "Service '$ServiceName' is not installed."
        return
    }

    Write-Host "Stopping service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    Write-Host "Removing service..."
    sc.exe delete $ServiceName | Out-Null

    Write-Host "Service '$ServiceName' removed."
}

function Get-ServiceStatus {
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $existing) {
        Write-Host "Service '$ServiceName' is not installed."
    } else {
        $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
        Write-Host "Service '$ServiceName': $($existing.Status)"
        if ($serviceInfo) {
            Write-Host "Binary path: $($serviceInfo.PathName)"
        }
    }
}

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "This script must be run as Administrator."
    exit 1
}

switch ($Action) {
    "install"   { Install-Service }
    "uninstall" { Uninstall-Service }
    "status"    { Get-ServiceStatus }
}
