[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Upload,
    [Parameter(Mandatory)][string]$IdentityName,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z')][string]$SourceCommit
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Store-Functions.ps1')
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\dotnet\packaging\ffmpeg-windows.lock.json') -Raw | ConvertFrom-Json
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('localmorph-store-verify-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $uploadDirectory = Join-Path $temporary 'upload'
    [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($Upload), $uploadDirectory)
    $bundles = @(Get-ChildItem -LiteralPath $uploadDirectory -File -Filter *.msixbundle)
    if ($bundles.Count -ne 1) { throw 'Store upload must contain exactly one MSIX bundle.' }
    $bundleDirectory = Join-Path $temporary 'bundle'
    [IO.Compression.ZipFile]::ExtractToDirectory($bundles[0].FullName, $bundleDirectory)
    [xml]$bundle = Get-Content -LiteralPath (Join-Path $bundleDirectory 'AppxMetadata\AppxBundleManifest.xml') -Raw
    if ($bundle.Bundle.Identity.Name -ne $IdentityName -or $bundle.Bundle.Identity.Publisher -ne $Publisher -or $bundle.Bundle.Identity.Version -ne $Version) { throw 'Unexpected Store bundle identity or version.' }
    $applications = @($bundle.Bundle.Packages.Package | Where-Object { $_.Type -eq 'application' })
    if (($applications.Architecture | Sort-Object) -join ',' -ne 'arm64,x64') { throw 'Bundle must contain exactly x64 and ARM64 application packages.' }
    foreach ($package in $applications) {
        $architecture = $package.Architecture
        $unpacked = Join-Path $temporary $architecture
        [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $bundleDirectory $package.FileName), $unpacked)
        [xml]$manifest = Get-Content -LiteralPath (Join-Path $unpacked 'AppxManifest.xml') -Raw
        if ($manifest.Package.Identity.Name -ne $IdentityName -or $manifest.Package.Identity.Publisher -ne $Publisher -or $manifest.Package.Identity.Version -ne $Version -or $manifest.Package.Identity.ProcessorArchitecture -ne $architecture) { throw "Unexpected $architecture Store identity, architecture, or version." }
        if (-not ($manifest.Package.Dependencies.PackageDependency | Where-Object { $_.Name -like 'Microsoft.WindowsAppRuntime*' })) { throw "$architecture package has no Windows App SDK framework dependency." }
        $runtime = @(Get-ChildItem -LiteralPath $unpacked -Recurse -File -Filter coreclr.dll)
        if ($runtime.Count -ne 1 -or (Get-PeArchitecture $runtime[0].FullName) -ne $architecture) { throw "$architecture package is not .NET self-contained." }
        foreach ($relative in @('tools\ffmpeg.exe', 'tools\ffprobe.exe', 'LocalMorph.Shell.dll')) {
            if ((Get-PeArchitecture (Join-Path $unpacked $relative)) -ne $architecture) { throw "$relative has the wrong architecture." }
        }
        foreach ($relative in @('tools\licenses\NOTICE.txt', 'tools\licenses\provenance.json', 'tools\licenses\ffmpeg-corresponding-source.zip', 'tools\licenses\localmorph-build.json')) {
            if (-not (Test-Path -LiteralPath (Join-Path $unpacked $relative))) { throw "Missing FFmpeg redistribution material: $relative" }
        }
        $licenses = Join-Path $unpacked 'tools\licenses'
        if ([string]::IsNullOrWhiteSpace((Get-Content -LiteralPath (Join-Path $licenses 'NOTICE.txt') -Raw))) { throw 'FFmpeg notices are empty.' }
        $provenance = Get-Content -LiteralPath (Join-Path $licenses 'provenance.json') -Raw | ConvertFrom-Json
        $pin = $lock.("win-$architecture")
        if ($provenance.binarySha256 -ne $pin.sha256 -or $provenance.binaryUrl -ne $pin.url) { throw 'FFmpeg provenance does not match the pinned build.' }
        Assert-FileHash (Join-Path $licenses 'ffmpeg-corresponding-source.zip') $provenance.correspondingSourceSha256
        $build = Get-Content -LiteralPath (Join-Path $licenses 'localmorph-build.json') -Raw | ConvertFrom-Json
        if ($build.sourceCommit -ne $SourceCommit -or $build.version -ne $Version -or $build.runtimeIdentifier -ne "win-$architecture") { throw 'Store application provenance does not match the approved source commit, version, or architecture.' }
        if (-not $manifest.SelectSingleNode("//*[local-name()='Verb' and @Clsid='761079A6-3C8C-4781-AF52-FB07D1C43C0A']")) { throw 'Explorer command registration is missing.' }
        if (Test-Path -LiteralPath (Join-Path $unpacked 'AppxSignature.p7x')) { throw 'Store upload unexpectedly contains a direct-release signature.' }
    }
    Write-Host 'Store artifact structure, identities, runtime, FFmpeg payloads, and native DLL architectures verified. Native ARM64 launch still requires an ARM64 device.'
}
finally { [IO.Directory]::Delete($temporary, $true) }
