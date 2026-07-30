param(
    [string]$BundlePath,

    [string]$CertificateThumbprint,
    [string]$TimestampUrl = "http://time.certum.pl",
    [switch]$Help
)

$ErrorActionPreference = "Stop"

if ($Help) {
    Write-Host @"
Signs a LibreGuard release bundle with the Certum cloud code-signing certificate.

Required setup:
  1. Connect SimplySign Desktop with the mobile SimplySign token.
  2. Confirm the certificate is visible in Cert:\CurrentUser\My.
  3. Pass its SHA-1 thumbprint with -CertificateThumbprint.

Example:
  .\scripts\sign-release.ps1 `
    -BundlePath .\publish\release-bundle `
    -CertificateThumbprint "ABCD1234..."

Parameters:
  -BundlePath               Root of the published bundle to sign.
  -CertificateThumbprint    SHA-1 thumbprint of the SimplySign certificate.
  -TimestampUrl             RFC 3161 timestamp server; Certum default is http://time.certum.pl.
"@
    exit 0
}

function Find-SignTool {
    $kitsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kitsRoot) {
        $candidate = Get-ChildItem $kitsRoot -Recurse -Filter signtool.exe |
            Where-Object { $_.FullName -match "\\x64\\signtool.exe$" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1

        if ($candidate) {
            return $candidate.FullName
        }
    }

    $fromPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($fromPath) {
        return $fromPath.Source
    }

    throw "signtool.exe was not found. Install the Windows SDK or add signtool.exe to PATH."
}

function Invoke-CheckedSignTool {
    param([string[]]$Arguments)

    & $script:SignTool @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed with exit code $LASTEXITCODE."
    }
}

function Invoke-BatchedSignTool {
    param(
        [string[]]$BaseArguments,
        [string[]]$FilePaths,
        [string]$OperationName,
        [int]$BatchSize = 50
    )

    $paths = @($FilePaths)
    for ($start = 0; $start -lt $paths.Count; $start += $BatchSize) {
        $end = [Math]::Min($start + $BatchSize - 1, $paths.Count - 1)
        $batch = @($paths[$start..$end])
        $batchNumber = [int]($start / $BatchSize) + 1
        $batchCount = [int][Math]::Ceiling($paths.Count / [double]$BatchSize)
        Write-Host "$OperationName batch $batchNumber/$batchCount ($($batch.Count) files)"
        Invoke-CheckedSignTool ($BaseArguments + $batch)
    }
}

function Normalize-Thumbprint {
    param([string]$Value)

    $normalized = ($Value -replace "\s", "").ToUpperInvariant()
    if ($normalized -notmatch "^[0-9A-F]{40}$") {
        throw "CertificateThumbprint must be a 40-character SHA-1 thumbprint."
    }

    return $normalized
}

function Find-SigningCertificate {
    param([string]$Thumbprint)

    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Thumbprint -eq $Thumbprint } |
        Select-Object -First 1

    if (-not $certificate) {
        throw "Certificate '$Thumbprint' was not found in Cert:\CurrentUser\My. Connect SimplySign Desktop first."
    }

    if (-not $certificate.HasPrivateKey) {
        throw "Certificate '$Thumbprint' is present but has no accessible private key. Connect SimplySign Desktop first."
    }

    if ($certificate.NotAfter -lt (Get-Date)) {
        throw "Certificate '$Thumbprint' expired on $($certificate.NotAfter.ToString('u'))."
    }

    $hasCodeSigningEku = @($certificate.EnhancedKeyUsageList) |
        Where-Object {
            $_.ObjectId.Value -eq "1.3.6.1.5.5.7.3.3" -or
            $_.FriendlyName -eq "Code Signing"
        }

    if (-not $hasCodeSigningEku) {
        throw "Certificate '$Thumbprint' does not contain the Code Signing enhanced key usage."
    }

    return $certificate
}

function Get-RelativeBundlePath {
    param([string]$FullPath)

    return $FullPath.Substring($resolvedBundle.Length).TrimStart('\', '/')
}

function Get-SignatureInfo {
    param(
        [System.IO.FileInfo]$File,
        [switch]$RequireTimestamp,
        [string]$ExpectedThumbprint
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $File.FullName
    if ($signature.Status -ne "Valid") {
        throw "Invalid Authenticode signature for '$($File.FullName)': $($signature.Status) - $($signature.StatusMessage)"
    }

    if ($ExpectedThumbprint) {
        $actualThumbprint = ($signature.SignerCertificate.Thumbprint -replace "\s", "").ToUpperInvariant()
        if ($actualThumbprint -ne $ExpectedThumbprint) {
            throw "Unexpected signer for '$($File.FullName)'. Expected '$ExpectedThumbprint', got '$actualThumbprint'."
        }
    }

    if ($RequireTimestamp -and -not $signature.TimeStamperCertificate) {
        throw "Signature for '$($File.FullName)' has no trusted timestamp."
    }

    return $signature
}

function Test-ExpectedSignature {
    param(
        [System.IO.FileInfo]$File,
        [string]$ExpectedThumbprint
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $File.FullName
    if ($signature.Status -ne "Valid") {
        return $false
    }

    $actualThumbprint = ($signature.SignerCertificate.Thumbprint -replace "\s", "").ToUpperInvariant()
    if ($actualThumbprint -ne $ExpectedThumbprint) {
        throw "Existing valid signature on '$($File.FullName)' belongs to '$actualThumbprint', not '$ExpectedThumbprint'."
    }

    return $null -ne $signature.TimeStamperCertificate
}

function Set-JsonPropertyValue {
    param(
        [object]$Object,
        [string]$Name,
        [object]$Value
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) {
        $property.Value = $Value
    } else {
        $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
    }
}

function Get-FileHashHex {
    param([string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

if ([string]::IsNullOrWhiteSpace($BundlePath)) {
    throw "Provide -BundlePath for the published release bundle."
}

$resolvedBundle = (Resolve-Path -LiteralPath $BundlePath).Path.TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $resolvedBundle -PathType Container)) {
    throw "BundlePath must be an existing directory: $BundlePath"
}

if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw "Provide -CertificateThumbprint for the certificate exposed by SimplySign Desktop."
}

$normalizedThumbprint = Normalize-Thumbprint $CertificateThumbprint
$script:SignTool = Find-SignTool
$script:SigningCertificate = Find-SigningCertificate $normalizedThumbprint

$allBundleFiles = @(Get-ChildItem -LiteralPath $resolvedBundle -Recurse -File)
$forbiddenExtensions = @(".cer", ".pem", ".p12", ".pfx", ".key")
$credentialFiles = @($allBundleFiles | Where-Object { $forbiddenExtensions -contains $_.Extension.ToLowerInvariant() })
if ($credentialFiles.Count -gt 0) {
    $paths = $credentialFiles | ForEach-Object { Get-RelativeBundlePath $_.FullName }
    throw "Certificate/key material must not be present in the release bundle: $($paths -join ', ')"
}

$bundlePrefix = [regex]::Escape($resolvedBundle + "\")
$ownedPathPattern = "^$bundlePrefix(app|service|installer)\\"
$thirdPartyPathPattern = "^$bundlePrefix(service\\bin|installers)\\"
$openVpnRuntimePattern = "^$bundlePrefixservice\\bin\\"

$ownedBinaries = @($allBundleFiles | Where-Object {
    $_.Extension.ToLowerInvariant() -in @(".exe", ".dll") -and
    $_.FullName -match $ownedPathPattern -and
    $_.FullName -notmatch $openVpnRuntimePattern -and
    $_.BaseName -like "LibreGuard*"
})

if ($ownedBinaries.Count -eq 0) {
    throw "No LibreGuard-owned EXE/DLL files were found under app, service, or installer."
}

$vendorFiles = @($allBundleFiles | Where-Object {
    $_.Extension.ToLowerInvariant() -in @(".exe", ".dll", ".msi") -and
    $_.FullName -match $thirdPartyPathPattern
})

$requiredVendorRelativePaths = @(
    "installers\openvpn\OpenVPN-Community-amd64.msi",
    "installers\webview2\MicrosoftEdgeWebView2Setup.exe"
)
foreach ($requiredVendorRelativePath in $requiredVendorRelativePaths) {
    $requiredVendorPath = Join-Path $resolvedBundle $requiredVendorRelativePath
    if (-not (Test-Path -LiteralPath $requiredVendorPath -PathType Leaf)) {
        throw "Required third-party release payload is missing: $requiredVendorRelativePath"
    }
}

$bundleInfoPath = Join-Path $resolvedBundle "bundle-info.json"
if (-not (Test-Path -LiteralPath $bundleInfoPath -PathType Leaf)) {
    throw "Published bundle is missing bundle-info.json. Rebuild it with publish-vm-bundle.ps1 before signing."
}

$scriptPathPattern = "^$bundlePrefixscripts\\"
$scriptFiles = @($allBundleFiles | Where-Object {
    $_.Extension -ieq ".ps1" -and $_.FullName -match $scriptPathPattern
})

$binariesToSign = @($ownedBinaries | Where-Object {
    -not (Test-ExpectedSignature -File $_ -ExpectedThumbprint $normalizedThumbprint)
})
if ($binariesToSign.Count -gt 0) {
    Write-Host "Signing $($binariesToSign.Count) LibreGuard binaries in batches."
    $signArguments = @(
        "sign", "/sha1", $normalizedThumbprint,
        "/fd", "SHA256",
        "/tr", $TimestampUrl,
        "/td", "SHA256",
        "/v"
    )
    Invoke-BatchedSignTool `
        -BaseArguments $signArguments `
        -FilePaths @($binariesToSign | ForEach-Object { $_.FullName }) `
        -OperationName "Signing binaries"
} else {
    Write-Host "All LibreGuard binaries already have the expected timestamped signature; skipping signing."
}

$scriptsToSign = @($scriptFiles | Where-Object {
    -not (Test-ExpectedSignature -File $_ -ExpectedThumbprint $normalizedThumbprint)
})
foreach ($file in $scriptsToSign) {
    Write-Host "Signing $(Get-RelativeBundlePath $file.FullName)"
    $result = Set-AuthenticodeSignature `
        -LiteralPath $file.FullName `
        -Certificate $script:SigningCertificate `
        -IncludeChain NotRoot `
        -TimestampServer $TimestampUrl `
        -HashAlgorithm SHA256

    if ($result.Status -notin @("Valid", "Unknown")) {
        throw "PowerShell signing failed for '$($file.FullName)': $($result.Status) - $($result.StatusMessage)"
    }
}

$signedBinaryInfo = @()
foreach ($file in $ownedBinaries) {
    Write-Host "Verifying $(Get-RelativeBundlePath $file.FullName)"
    $signature = Get-SignatureInfo -File $file -RequireTimestamp -ExpectedThumbprint $normalizedThumbprint
    $signedBinaryInfo += [ordered]@{
        path = Get-RelativeBundlePath $file.FullName
        sha256 = Get-FileHashHex $file.FullName
        signer = $signature.SignerCertificate.Subject
    }
}

$signedScriptInfo = @()
foreach ($file in $scriptFiles) {
    $signature = Get-SignatureInfo -File $file -RequireTimestamp -ExpectedThumbprint $normalizedThumbprint
    $signedScriptInfo += [ordered]@{
        path = Get-RelativeBundlePath $file.FullName
        sha256 = Get-FileHashHex $file.FullName
        signer = $signature.SignerCertificate.Subject
    }
}

$vendorInfo = @()
foreach ($file in $vendorFiles) {
    Write-Host "Verifying $(Get-RelativeBundlePath $file.FullName)"
    $signature = Get-SignatureInfo -File $file
    $vendorInfo += [ordered]@{
        path = Get-RelativeBundlePath $file.FullName
        sha256 = Get-FileHashHex $file.FullName
        signer = $signature.SignerCertificate.Subject
    }
}

$signedUtc = (Get-Date).ToUniversalTime().ToString("o")
$bundleInfo = Get-Content -LiteralPath $bundleInfoPath -Raw | ConvertFrom-Json
Set-JsonPropertyValue -Object $bundleInfo -Name "appDllSha256" -Value (Get-FileHashHex (Join-Path $resolvedBundle "app\LibreGuard VPN Desktop.dll"))
Set-JsonPropertyValue -Object $bundleInfo -Name "installerSha256" -Value (Get-FileHashHex (Join-Path $resolvedBundle "installer\LibreGuard.Installer.exe"))
Set-JsonPropertyValue -Object $bundleInfo -Name "signedUtc" -Value $signedUtc
Set-JsonPropertyValue -Object $bundleInfo -Name "signingCertificateThumbprint" -Value $normalizedThumbprint
Set-JsonPropertyValue -Object $bundleInfo -Name "signingCertificateSubject" -Value $script:SigningCertificate.Subject
Set-JsonPropertyValue -Object $bundleInfo -Name "timestampUrl" -Value $TimestampUrl
$bundleInfo | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $bundleInfoPath -Encoding UTF8

$report = [ordered]@{
    signedUtc = $signedUtc
    certificateThumbprint = $normalizedThumbprint
    certificateSubject = $script:SigningCertificate.Subject
    timestampUrl = $TimestampUrl
    ownedBinaries = @($signedBinaryInfo)
    powershellScripts = @($signedScriptInfo)
    vendorFiles = @($vendorInfo)
    bundleInfoSha256 = Get-FileHashHex $bundleInfoPath
}
$reportPath = Join-Path $resolvedBundle "signing-report.json"
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host "Signing verification complete."
Write-Host "Signing report: $reportPath"
