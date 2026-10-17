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
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('localmorph-store-verify-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $uploadDirectory = Join-Path $temporary 'upload'
    [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($Upload), $uploadDirectory)
    $bundles = @(Get-ChildItem -LiteralPath $uploadDirectory -File -Filter *.msixbundle)
    if ($bundles.Count -ne 1) { throw 'Store upload must contain exactly one MSIX bundle.' }
    $symbolPackages = @(Get-ChildItem -LiteralPath $uploadDirectory -File -Filter *.appxsym)
    if ($symbolPackages.Count -ne 2) { throw 'Store upload must contain x64 and ARM64 crash-symbol archives.' }
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
        if ((Get-PeArchitecture (Join-Path $unpacked 'LocalMorph.Shell.dll')) -ne $architecture) { throw 'LocalMorph.Shell.dll has the wrong architecture.' }
        $bundledTools = @(Get-ChildItem -LiteralPath $unpacked -Recurse -File |
            Where-Object { $_.Name -in @('ffmpeg.exe', 'ffprobe.exe') })
        if ($bundledTools.Count) { throw 'Store packages must not bundle FFmpeg or FFprobe; install these tools separately.' }
        $buildPath = Join-Path $unpacked 'localmorph-build.json'
        if (-not (Test-Path -LiteralPath $buildPath -PathType Leaf)) { throw 'Missing Store application provenance: localmorph-build.json' }
        $build = Get-Content -LiteralPath $buildPath -Raw | ConvertFrom-Json
        if ($build.mediaTools -ne 'external') {
            throw 'Store application provenance must declare external media tools.'
        }
        if ($build.sourceCommit -ne $SourceCommit -or $build.version -ne $Version -or $build.runtimeIdentifier -ne "win-$architecture") { throw 'Store application provenance does not match the approved source commit, version, or architecture.' }
        if (-not $manifest.SelectSingleNode("//*[local-name()='Verb' and @Clsid='761079A6-3C8C-4781-AF52-FB07D1C43C0A']")) { throw 'Explorer command registration is missing.' }
        if (Test-Path -LiteralPath (Join-Path $unpacked 'AppxSignature.p7x')) { throw 'Store upload unexpectedly contains a direct-release signature.' }
        if (Get-ChildItem -LiteralPath $unpacked -Recurse -File -Filter *.pdb) { throw 'Debug symbols belong in the Store symbol archive, not the installed application.' }
        $symbolPackage = Join-Path $uploadDirectory "LocalMorph-$Version-win-$architecture.appxsym"
        if (-not (Test-Path -LiteralPath $symbolPackage)) { throw "Missing $architecture crash-symbol archive for version $Version." }
        $symbolDirectory = Join-Path $temporary "symbols-$architecture"
        New-Item -ItemType Directory -Path $symbolDirectory | Out-Null
        [IO.Compression.ZipFile]::ExtractToDirectory($symbolPackage, $symbolDirectory)
        foreach ($module in @('LocalMorph.App', 'LocalMorph.Core', 'LocalMorph.Shell')) {
            $pdb = @(Get-ChildItem -LiteralPath $symbolDirectory -Recurse -File -Filter "$module.pdb")
            if ($pdb.Count -ne 1) { throw "Expected exactly one $architecture $module PDB." }
            Assert-PdbMatchesBinary -Binary (Join-Path $unpacked "$module.dll") -Pdb $pdb[0].FullName
        }
    }
    Write-Host 'Store identities, runtime, external media-tool mode, native architectures, and matching managed/native crash symbols verified. Native ARM64 launch still requires an ARM64 device.'
}
finally { [IO.Directory]::Delete($temporary, $true) }
