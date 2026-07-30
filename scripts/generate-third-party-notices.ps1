[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [string[]]$ProjectAssetsPaths,

    [string]$Runtime = "win-x64",

    [string[]]$RuntimeOutputPaths = @(),

    [string]$OpenVpnManifestPath,

    [string]$OpenVpnPayloadPath,

    [string]$OpenVpnNoticesPath,

    [string]$WebView2ManifestPath,

    [string]$WebView2PayloadPath,

    [string]$WebView2NoticesPath,

    [string]$InnoCompilerPath,

    [switch]$SourceOnly
)

$ErrorActionPreference = "Stop"

function Get-NuGetGlobalPackagesPath {
    $line = dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0 -or $line -notmatch '^global-packages:\s*(.+)$') {
        throw "Unable to locate the NuGet global-packages folder."
    }

    return $Matches[1].Trim()
}

function Get-PackageComponents {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$AssetsPaths,

        [Parameter(Mandatory = $true)]
        [string]$GlobalPackagesPath
    )

    $components = @{}
    foreach ($assetsPath in $AssetsPaths) {
        if (-not (Test-Path -LiteralPath $assetsPath)) {
            throw "Restore assets file not found: $assetsPath"
        }

        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
        foreach ($target in $assets.targets.PSObject.Properties) {
            foreach ($targetLibrary in $target.Value.PSObject.Properties) {
                $library = ($assets.libraries.PSObject.Properties | Where-Object Name -eq $targetLibrary.Name | Select-Object -First 1).Value
                if ($null -eq $library -or $library.type -ne 'package' -or $components.ContainsKey($targetLibrary.Name)) {
                    continue
                }

                $slash = $targetLibrary.Name.LastIndexOf('/')
                if ($slash -lt 1) {
                    throw "Unexpected package key in restore assets: $($targetLibrary.Name)"
                }

                $packageId = $targetLibrary.Name.Substring(0, $slash)
                $packageVersion = $targetLibrary.Name.Substring($slash + 1)
                $packagePath = Join-Path $GlobalPackagesPath $library.path.ToLowerInvariant()
                $nuspecPath = Get-ChildItem -LiteralPath $packagePath -Filter '*.nuspec' -File | Select-Object -First 1 -ExpandProperty FullName
                if ([string]::IsNullOrWhiteSpace($nuspecPath)) {
                    throw "NuGet package metadata was not found for $packageId $packageVersion."
                }

                [xml]$nuspec = Get-Content -LiteralPath $nuspecPath -Raw
                $metadata = $nuspec.package.metadata
                $licenseKind = $null
                $licenseValue = $null
                if ($null -ne $metadata.license) {
                    $licenseKind = $metadata.license.type
                    $licenseValue = $metadata.license.'#text'
                } elseif (-not [string]::IsNullOrWhiteSpace($metadata.licenseUrl)) {
                    $licenseKind = 'url'
                    $licenseValue = $metadata.licenseUrl
                }

                if ([string]::IsNullOrWhiteSpace($licenseKind) -or [string]::IsNullOrWhiteSpace($licenseValue)) {
                    throw "NuGet package $packageId $packageVersion does not declare license metadata."
                }

                $components[$targetLibrary.Name] = [pscustomobject]@{
                    Id = $packageId
                    Version = $packageVersion
                    Authors = $metadata.authors
                    LicenseKind = $licenseKind
                    LicenseValue = $licenseValue
                    PackagePath = $packagePath
                }
            }
        }
    }

    return @($components.Values | Sort-Object Id, Version)
}

function Get-MITLicenseText {
    return @'
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
'@
}

function Get-PackageLicenseText {
    param([Parameter(Mandatory = $true)]$Component)

    switch ($Component.LicenseKind.ToLowerInvariant()) {
        'expression' {
            if ($Component.LicenseValue -eq 'MIT') {
                return Get-MITLicenseText
            }

            throw "NuGet package $($Component.Id) $($Component.Version) uses unsupported license expression '$($Component.LicenseValue)'. Add explicit handling before publishing."
        }
        'file' {
            $licensePath = Get-ChildItem -LiteralPath $Component.PackagePath -Recurse -File |
                Where-Object { $_.Name -ieq $Component.LicenseValue } |
                Select-Object -First 1 -ExpandProperty FullName
            if ([string]::IsNullOrWhiteSpace($licensePath)) {
                throw "NuGet package $($Component.Id) $($Component.Version) declares license file '$($Component.LicenseValue)', but the file is missing."
            }

            return Get-Content -LiteralPath $licensePath -Raw
        }
        default {
            throw "NuGet package $($Component.Id) $($Component.Version) only provides a license URL. Add its exact notice material before publishing."
        }
    }
}

function Get-RuntimePackageDirectories {
    param(
        [Parameter(Mandatory = $true)]
        [string]$GlobalPackagesPath,

        [Parameter(Mandatory = $true)]
        [string]$RuntimeIdentifier,

        [string[]]$PublishedOutputPaths
    )

    $runtimePackageRoot = Join-Path $GlobalPackagesPath "microsoft.netcore.app.runtime.$($RuntimeIdentifier.ToLowerInvariant())"
    if (-not (Test-Path -LiteralPath $runtimePackageRoot)) {
        throw "The .NET runtime package for $RuntimeIdentifier has not been restored."
    }

    $versions = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($publishedOutputPath in $PublishedOutputPaths) {
        if (-not (Test-Path -LiteralPath $publishedOutputPath)) {
            continue
        }

        foreach ($runtimeConfigPath in Get-ChildItem -LiteralPath $publishedOutputPath -Filter '*.runtimeconfig.json' -File) {
            $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath.FullName -Raw | ConvertFrom-Json
            $version = $runtimeConfig.runtimeOptions.framework.version
            if (-not [string]::IsNullOrWhiteSpace($version)) {
                [void]$versions.Add($version)
            }
        }
    }

    if ($versions.Count -eq 0) {
        $latest = Get-ChildItem -LiteralPath $runtimePackageRoot -Directory |
            Sort-Object { [version]$_.Name } -Descending |
            Select-Object -First 1
        if ($null -eq $latest) {
            throw "No restored .NET runtime versions were found for $RuntimeIdentifier."
        }

        [void]$versions.Add($latest.Name)
    }

    return @($versions | ForEach-Object {
        $directory = Join-Path $runtimePackageRoot $_
        if (-not (Test-Path -LiteralPath $directory)) {
            throw "The restored .NET runtime package version $_ for $RuntimeIdentifier is unavailable."
        }
        $directory
    })
}

function Get-RequiredExternalComponent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$ManifestPath,

        [Parameter(Mandatory = $true)]
        [string]$PayloadPath,

        [Parameter(Mandatory = $true)]
        [string]$NoticesPath
    )

    foreach ($path in @($ManifestPath, $PayloadPath, $NoticesPath)) {
        if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path)) {
            throw "$Name release material is incomplete."
        }
    }

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($manifest.version) -or $manifest.version -match 'replace' -or
        [string]::IsNullOrWhiteSpace($manifest.sha256) -or $manifest.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw "$Name manifest does not identify a final version and SHA-256."
    }

    $actualHash = (Get-FileHash -LiteralPath $PayloadPath -Algorithm SHA256).Hash
    if ($actualHash -ne $manifest.sha256) {
        throw "$Name payload SHA-256 does not match its manifest."
    }

    $noticeFiles = Get-ChildItem -LiteralPath $NoticesPath -Recurse -File |
        Where-Object { $_.Name -ne '.gitkeep' } |
        Sort-Object FullName
    if (@($noticeFiles).Count -eq 0) {
        throw "$Name notice material is missing for version $($manifest.version)."
    }

    return [pscustomobject]@{
        Version = $manifest.version
        SourceUrl = $manifest.sourceUrl
        NoticeFiles = @($noticeFiles)
    }
}

function Get-InnoSetupComponent {
    param([string]$CompilerPath)

    $candidates = @($CompilerPath) + @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    $compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($compiler)) {
        throw "Inno Setup compiler was not found."
    }

    $licensePath = Join-Path (Split-Path -Parent $compiler) 'license.txt'
    if (-not (Test-Path -LiteralPath $licensePath)) {
        throw "Inno Setup license file was not found next to $compiler."
    }

    return [pscustomobject]@{
        Version = (Get-Item -LiteralPath $compiler).VersionInfo.ProductVersion
        LicenseText = Get-Content -LiteralPath $licensePath -Raw
    }
}

$globalPackagesPath = Get-NuGetGlobalPackagesPath
$components = Get-PackageComponents -AssetsPaths $ProjectAssetsPaths -GlobalPackagesPath $globalPackagesPath
$runtimeDirectories = Get-RuntimePackageDirectories -GlobalPackagesPath $globalPackagesPath -RuntimeIdentifier $Runtime -PublishedOutputPaths $RuntimeOutputPaths

$sections = [System.Collections.Generic.List[string]]::new()
$sections.Add('LibreGuard VPN Third-Party Notices')
$sections.Add('===================================')
$sections.Add('')
$sections.Add('This file identifies the third-party components included in this build and preserves their license material.')
$sections.Add('')
$sections.Add('NuGet components')
$sections.Add('----------------')
foreach ($component in $components) {
    $sections.Add("- $($component.Id) $($component.Version) — $($component.Authors) — $($component.LicenseValue)")
}

foreach ($licenseGroup in $components | Group-Object LicenseKind, LicenseValue) {
    $representative = $licenseGroup.Group | Select-Object -First 1
    $sections.Add('')
    $sections.Add("License material: $($representative.LicenseValue)")
    $sections.Add(('=' * (18 + $representative.LicenseValue.Length)))
    $sections.Add((Get-PackageLicenseText -Component $representative).TrimEnd())
}

foreach ($runtimeDirectory in $runtimeDirectories) {
    $runtimeVersion = Split-Path -Leaf $runtimeDirectory
    $licensePath = Join-Path $runtimeDirectory 'LICENSE.TXT'
    $noticesPath = Join-Path $runtimeDirectory 'THIRD-PARTY-NOTICES.TXT'
    if (-not (Test-Path -LiteralPath $licensePath) -or -not (Test-Path -LiteralPath $noticesPath)) {
        throw ".NET runtime notice material is incomplete for $Runtime $runtimeVersion."
    }

    $sections.Add('')
    $sections.Add("Microsoft .NET Runtime $runtimeVersion ($Runtime)")
    $sections.Add(('=' * (32 + $runtimeVersion.Length + $Runtime.Length)))
    $sections.Add((Get-Content -LiteralPath $licensePath -Raw).TrimEnd())
    $sections.Add('')
    $sections.Add((Get-Content -LiteralPath $noticesPath -Raw).TrimEnd())
}

if (-not $SourceOnly) {
    $openVpn = Get-RequiredExternalComponent -Name 'OpenVPN' -ManifestPath $OpenVpnManifestPath -PayloadPath $OpenVpnPayloadPath -NoticesPath $OpenVpnNoticesPath
    $sections.Add('')
    $sections.Add("OpenVPN® $($openVpn.Version)")
    $sections.Add(('=' * (11 + $openVpn.Version.Length)))
    $sections.Add('OpenVPN® is a registered trademark of OpenVPN Inc.')
    $sections.Add("Source: $($openVpn.SourceUrl)")
    foreach ($noticeFile in $openVpn.NoticeFiles) {
        $sections.Add('')
        $sections.Add("Source file: $($noticeFile.Name)")
        $sections.Add((Get-Content -LiteralPath $noticeFile.FullName -Raw).TrimEnd())
    }

    $webView2 = Get-RequiredExternalComponent -Name 'Microsoft Edge WebView2 Evergreen Bootstrapper' -ManifestPath $WebView2ManifestPath -PayloadPath $WebView2PayloadPath -NoticesPath $WebView2NoticesPath
    $sections.Add('')
    $sections.Add("Microsoft Edge WebView2 Evergreen Bootstrapper ($($webView2.Version))")
    $sections.Add(('=' * (48 + $webView2.Version.Length)))
    $sections.Add("Source: $($webView2.SourceUrl)")
    foreach ($noticeFile in $webView2.NoticeFiles) {
        $sections.Add('')
        $sections.Add("Source file: $($noticeFile.Name)")
        $sections.Add((Get-Content -LiteralPath $noticeFile.FullName -Raw).TrimEnd())
    }

    $innoSetup = Get-InnoSetupComponent -CompilerPath $InnoCompilerPath
    $sections.Add('')
    $sections.Add("Inno Setup $($innoSetup.Version)")
    $sections.Add(('=' * (12 + $innoSetup.Version.Length)))
    $sections.Add($innoSetup.LicenseText.TrimEnd())
}

$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

[System.IO.File]::WriteAllText(
    $resolvedOutputPath,
    ($sections -join [Environment]::NewLine) + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))
