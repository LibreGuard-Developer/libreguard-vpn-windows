[CmdletBinding()]
param(
    [string]$SourceUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$payloadDirectory = Join-Path $repoRoot "installers\webview2"
$bootstrapperPath = Join-Path $payloadDirectory "MicrosoftEdgeWebView2Setup.exe"
$manifestPath = Join-Path $payloadDirectory "manifest.json"

New-Item -ItemType Directory -Force -Path $payloadDirectory | Out-Null
Invoke-WebRequest -Uri $SourceUrl -OutFile $bootstrapperPath -UseBasicParsing

$hash = (Get-FileHash $bootstrapperPath -Algorithm SHA256).Hash
$manifest = [ordered]@{
    version = "Evergreen"
    fileName = "MicrosoftEdgeWebView2Setup.exe"
    sha256 = $hash
    sourceUrl = $SourceUrl
}

$manifest | ConvertTo-Json | Set-Content -Encoding UTF8 $manifestPath
Write-Host "Downloaded WebView2 Evergreen bootstrapper and updated:" -ForegroundColor Green
Write-Host "  $manifestPath"
Write-Host "SHA-256: $hash"
