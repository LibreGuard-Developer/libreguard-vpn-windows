param(
    [string]$MsiPath = ".\installers\openvpn\OpenVPN-Community-amd64.msi",
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$SourceUrl = "https://openvpn.net/community-downloads/"
)

$ErrorActionPreference = "Stop"

$resolvedMsi = Resolve-Path $MsiPath
$sha256 = (Get-FileHash -Path $resolvedMsi -Algorithm SHA256).Hash
$manifestPath = Join-Path (Split-Path -Parent $resolvedMsi) "manifest.json"

$manifest = [ordered]@{
    version = $Version
    fileName = Split-Path -Leaf $resolvedMsi
    sha256 = $sha256
    sourceUrl = $SourceUrl
}

$manifest | ConvertTo-Json -Depth 3 | Set-Content -Path $manifestPath -Encoding utf8NoBOM

Write-Host "Updated OpenVPN manifest:"
Write-Host "  $manifestPath"
Write-Host "SHA-256:"
Write-Host "  $sha256"
