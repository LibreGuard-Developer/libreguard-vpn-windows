# Publishes the desktop app and VPN service into one VM-friendly bundle.
# Run from anywhere; paths are resolved relative to this script.

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [bool]$SelfContained = $true,
    [string]$OpenVpnPayloadPath,
    [string]$OutputPath,
    [switch]$AllowMissingGoogleOAuthClientId
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw @"
dotnet was not found on this machine.
Run this script on your development machine with the .NET SDK installed, then copy the resulting bundle to the target VM.
If you want to validate a copied bundle on the VM, run the installer from .\installer\LibreGuard.Installer.exe instead of publishing again.
"@
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    $capturedOutput = [System.Collections.Generic.List[string]]::new()
    & $Command 2>&1 | ForEach-Object {
        $line = $_.ToString()
        $capturedOutput.Add($line)
        Write-Output $_
    }
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $details = $capturedOutput -join [Environment]::NewLine
        if ([string]::IsNullOrWhiteSpace($details)) {
            $details = "The command produced no diagnostic output."
        }

        throw "$FailureMessage (exit code $exitCode)`n$details"
    }
}

if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot "LibreGuard VPN Desktop\bin\$Configuration\net10.0-windows10.0.17763.0\$Runtime\publish"
}

function Get-ScopedEnvironmentVariable {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Names
    )

    foreach ($name in $Names) {
        foreach ($target in @(
            [EnvironmentVariableTarget]::Process,
            [EnvironmentVariableTarget]::User,
            [EnvironmentVariableTarget]::Machine
        )) {
            try {
                $value = [Environment]::GetEnvironmentVariable($name, $target)
                if (-not [string]::IsNullOrWhiteSpace($value)) {
                    return $value.Trim()
                }
            } catch {
                # Continue to the next scope when a scope is unavailable.
            }
        }
    }

    return $null
}

function Test-LooksLikeGoogleClientId {
    param([string]$Value)

    return -not [string]::IsNullOrWhiteSpace($Value) -and
        $Value.Trim().EndsWith('.apps.googleusercontent.com', [System.StringComparison]::OrdinalIgnoreCase)
}

if (Test-Path $OutputPath) {
    Write-Host "Cleaning existing bundle output at:"
    Write-Host "  $OutputPath"
    Remove-Item -LiteralPath $OutputPath -Recurse -Force
}

$appProject = Join-Path $repoRoot "LibreGuard VPN Desktop\LibreGuard VPN Desktop.csproj"
$serviceProject = Join-Path $repoRoot "LibreGuard.VpnService\LibreGuard.VpnService.csproj"
$setupHelperProject = Join-Path $repoRoot "LibreGuard.SetupHelper\LibreGuard.SetupHelper.csproj"
$installerProject = Join-Path $repoRoot "LibreGuard.Installer\LibreGuard.Installer.csproj"
$thirdPartyNoticesScript = Join-Path $PSScriptRoot "generate-third-party-notices.ps1"
$installersSource = Join-Path $repoRoot "installers"

$appOut = Join-Path $OutputPath "app"
$serviceOut = Join-Path $OutputPath "service"
$setupHelperOut = Join-Path $appOut "setup"
$installerOut = Join-Path $OutputPath "installer"
$installersOut = Join-Path $OutputPath "installers"
$licensesOut = Join-Path $OutputPath "licenses"

New-Item -ItemType Directory -Force -Path $appOut, $serviceOut, $setupHelperOut, $installerOut, $installersOut, $licensesOut | Out-Null

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }
$googleClientId = Get-ScopedEnvironmentVariable -Names @('LIBREGUARD_GOOGLE_CLIENT_ID')
if ($googleClientId -and -not (Test-LooksLikeGoogleClientId $googleClientId)) {
    throw "LIBREGUARD_GOOGLE_CLIENT_ID is not a valid Google OAuth client ID."
}

$googleClientIdArgument = if ($googleClientId) { "-p:InjectedGoogleClientId=$googleClientId" } else { $null }
$allowMissingGoogleClientIdArgument = if ($AllowMissingGoogleOAuthClientId) { "-p:AllowMissingGoogleOAuthClientId=true" } else { $null }

Write-Host "Publishing desktop app to:"
Write-Host "  $appOut"
Invoke-CheckedCommand `
    -FailureMessage "Desktop publish failed." `
    -Command {
        dotnet publish $appProject `
            -c $Configuration `
            -r $Runtime `
            --self-contained $selfContainedValue `
            -o $appOut `
            -p:NuGetAudit=false `
            $googleClientIdArgument `
            $allowMissingGoogleClientIdArgument
    }

function Assert-BundleMarker {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AppDllPath
    )

    if (-not (Test-Path $AppDllPath)) {
        throw "Published app DLL not found at '$AppDllPath'."
    }

    $bytes = [System.IO.File]::ReadAllBytes($AppDllPath)
    $latin1Encoding = [System.Text.Encoding]::GetEncoding(28591)
    $text = $latin1Encoding.GetString($bytes)
    foreach ($marker in @("GroupedServers", "ServerSummary", "DataUsageProgressStyle", "LoadToBrush")) {
        if ($text -notlike "*$marker*") {
            throw "Published app DLL at '$AppDllPath' does not contain expected marker '$marker'. Rebuild the desktop app before publishing."
        }
    }
}

Assert-BundleMarker -AppDllPath (Join-Path $appOut "LibreGuard VPN Desktop.dll")

$googleOAuthConfigPath = Join-Path $appOut "google-oauth-client.json"
$googleOAuthConfigIsValid = $false
if (Test-Path $googleOAuthConfigPath) {
    try {
        $googleOAuthConfig = Get-Content $googleOAuthConfigPath -Raw | ConvertFrom-Json
        $propertyNames = @($googleOAuthConfig.PSObject.Properties.Name)
        $googleOAuthConfigIsValid =
            $propertyNames.Count -eq 1 -and
            $propertyNames[0] -eq 'clientId' -and
            (Test-LooksLikeGoogleClientId $googleOAuthConfig.clientId)
    } catch {
        $googleOAuthConfigIsValid = $false
    }
}

if (-not $googleOAuthConfigIsValid) {
    if ($AllowMissingGoogleOAuthClientId) {
        Write-Warning "Published app does not contain a usable Google OAuth client ID. This was explicitly allowed for a test artifact."
    } else {
        throw "Published app is missing a valid ID-only google-oauth-client.json. Set the user environment variable LIBREGUARD_GOOGLE_CLIENT_ID and publish again."
    }
}

Write-Host ""
Write-Host "Publishing VPN service to:"
Write-Host "  $serviceOut"
Invoke-CheckedCommand `
    -FailureMessage "VPN service publish failed." `
    -Command {
        dotnet publish $serviceProject `
            -c $Configuration `
            -r $Runtime `
            --self-contained $selfContainedValue `
            -o $serviceOut `
            -p:NuGetAudit=false
    }

Write-Host ""
Write-Host "Publishing setup helper to:"
Write-Host "  $setupHelperOut"
Invoke-CheckedCommand `
    -FailureMessage "Setup helper publish failed." `
    -Command {
        dotnet publish $setupHelperProject `
            -c $Configuration `
            -r $Runtime `
            --self-contained $selfContainedValue `
            -o $setupHelperOut `
            -p:NuGetAudit=false
    }

Write-Host ""
Write-Host "Publishing production installer to:"
Write-Host "  $installerOut"
Invoke-CheckedCommand `
    -FailureMessage "Production installer publish failed." `
    -Command {
        dotnet publish $installerProject `
            -c $Configuration `
            -r $Runtime `
            --self-contained $selfContainedValue `
            -o $installerOut `
            -p:NuGetAudit=false
    }

if ($OpenVpnPayloadPath) {
    $resolvedOpenVpnPayloadPath = (Resolve-Path $OpenVpnPayloadPath).Path
    $openVpnPayloadItem = Get-Item $resolvedOpenVpnPayloadPath
    $openVpnSourceDir = if ($openVpnPayloadItem.PSIsContainer) {
        $resolvedOpenVpnPayloadPath
    } else {
        Split-Path -Parent $resolvedOpenVpnPayloadPath
    }

    $openVpnExePath = Join-Path $openVpnSourceDir "openvpn.exe"
    if (-not (Test-Path $openVpnExePath)) {
        throw "OpenVPN payload must be openvpn.exe or a folder containing openvpn.exe. Path: $OpenVpnPayloadPath"
    }

    $openVpnOut = Join-Path $serviceOut "bin"
    New-Item -ItemType Directory -Force -Path $openVpnOut | Out-Null

    Write-Host ""
    Write-Host "Copying OpenVPN runtime to:"
    Write-Host "  $openVpnOut"
    robocopy $openVpnSourceDir $openVpnOut /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -gt 7) {
        throw "Failed to copy OpenVPN runtime to '$openVpnOut' (robocopy exit code $LASTEXITCODE)."
    }
}

if (Test-Path $installersSource) {
    Write-Host ""
    Write-Host "Copying installer payloads to:"
    Write-Host "  $installersOut"
    robocopy $installersSource $installersOut /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -gt 7) {
        throw "Failed to copy installer payloads to '$installersOut' (robocopy exit code $LASTEXITCODE)."
    }
}

$webView2Bootstrapper = Join-Path $installersOut "webview2\MicrosoftEdgeWebView2Setup.exe"
$webView2ManifestPath = Join-Path $installersOut "webview2\manifest.json"
if (-not (Test-Path $webView2Bootstrapper) -or -not (Test-Path $webView2ManifestPath)) {
    throw "WebView2 Evergreen bootstrapper payload is incomplete. Run .\scripts\update-webview2-manifest.ps1 before publishing."
}

$webView2Manifest = Get-Content $webView2ManifestPath -Raw | ConvertFrom-Json
$webView2ActualHash = (Get-FileHash $webView2Bootstrapper -Algorithm SHA256).Hash
if ([string]::IsNullOrWhiteSpace($webView2Manifest.sha256) -or
    $webView2Manifest.sha256 -match "replace" -or
    $webView2ActualHash -ne $webView2Manifest.sha256) {
    throw "WebView2 Evergreen bootstrapper checksum does not match installers\webview2\manifest.json."
}

Write-Host ""
Write-Host "Generating third-party notices at:"
Write-Host "  $licensesOut"
& $thirdPartyNoticesScript `
    -OutputPath (Join-Path $licensesOut "THIRD-PARTY-NOTICES.txt") `
    -ProjectAssetsPaths @(
        (Join-Path $repoRoot "LibreGuard VPN Desktop\obj\project.assets.json"),
        (Join-Path $repoRoot "LibreGuard.VpnService\obj\project.assets.json"),
        (Join-Path $repoRoot "LibreGuard.SetupHelper\obj\project.assets.json"),
        (Join-Path $repoRoot "LibreGuard.Installer\obj\project.assets.json")
    ) `
    -Runtime $Runtime `
    -RuntimeOutputPaths @($appOut, $serviceOut, $setupHelperOut, $installerOut) `
    -OpenVpnManifestPath (Join-Path $installersOut "openvpn\manifest.json") `
    -OpenVpnPayloadPath (Join-Path $installersOut "openvpn\OpenVPN-Community-amd64.msi") `
    -OpenVpnNoticesPath (Join-Path $installersOut "openvpn\notices") `
    -WebView2ManifestPath $webView2ManifestPath `
    -WebView2PayloadPath $webView2Bootstrapper `
    -WebView2NoticesPath (Join-Path $installersOut "webview2\notices")

$thirdPartyNoticesPath = Join-Path $licensesOut "THIRD-PARTY-NOTICES.txt"
if (-not (Test-Path -LiteralPath $thirdPartyNoticesPath)) {
    throw "Third-party notice generation did not produce $thirdPartyNoticesPath."
}

$bundleInfo = [ordered]@{
    generatedUtc = (Get-Date).ToUniversalTime().ToString("o")
    configuration = $Configuration
    runtime = $Runtime
    selfContained = $SelfContained
    appDllSha256 = (Get-FileHash (Join-Path $appOut "LibreGuard VPN Desktop.dll") -Algorithm SHA256).Hash
    installerSha256 = (Get-FileHash (Join-Path $installerOut "LibreGuard.Installer.exe") -Algorithm SHA256).Hash
    thirdPartyNoticesSha256 = (Get-FileHash $thirdPartyNoticesPath -Algorithm SHA256).Hash
}

$bundleInfo | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $OutputPath "bundle-info.json")

Write-Host ""
Write-Host "VM bundle ready:"
Write-Host "  $OutputPath"
Write-Host ""
Write-Host "Fresh files:"
Get-ChildItem $appOut, $serviceOut |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 20 Name, LastWriteTime, Length |
    Format-Table -AutoSize
