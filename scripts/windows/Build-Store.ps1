[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\store'),
    [string]$IdentityName = $env:MICROSOFT_STORE_IDENTITY_NAME,
    [string]$Publisher = $env:MICROSOFT_STORE_PUBLISHER,
    [string]$PublisherDisplayName = $env:MICROSOFT_STORE_PUBLISHER_DISPLAY_NAME
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Store-Functions.ps1')
$version = Get-StoreVersion $Tag
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Store symbol packaging requires Visual Studio Build Tools with Desktop development with C++.' }
$pdbTool = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC\Tools\MSVC\**\bin\Hostx64\x64\mspdbcmf.exe' |
    Sort-Object -Descending | Select-Object -First 1
if ($LASTEXITCODE -ne 0 -or -not $pdbTool -or -not (Test-Path -LiteralPath $pdbTool)) { throw 'mspdbcmf.exe was not found. Install the Visual C++ x64 tools required for Store symbol packaging.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sourceCommit = git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the Store application source commit.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$metadata = Join-Path $output 'metadata'
foreach ($rid in @('win-x64', 'win-arm64')) {
    $directory = Join-Path $metadata $rid
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    @{ sourceCommit = $sourceCommit; version = $version; runtimeIdentifier = $rid; mediaTools = 'external' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'localmorph-build.json') -Encoding utf8
}
$manifestPath = Join-Path $root 'dotnet\src\LocalMorph.App\Platforms\Windows\Package.appxmanifest'
$originalManifest = [IO.File]::ReadAllBytes($manifestPath)
$bundleInputs = Join-Path $output ('bundle-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $bundleInputs | Out-Null
$symbolPackages = @()
try {
    New-StoreManifest -DirectPath $manifestPath -TemplatePath (Join-Path ([IO.Path]::GetDirectoryName($manifestPath)) 'Package.Store.appxmanifest') -DestinationPath $manifestPath -Name $IdentityName -Publisher $Publisher -PublisherDisplayName $PublisherDisplayName -Version $version
    foreach ($architecture in @('x64', 'ARM64')) {
        $rid = 'win-' + $architecture.ToLowerInvariant()
        $packageDirectory = Join-Path $output ('package-' + $rid + '-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $packageDirectory | Out-Null
        dotnet publish (Join-Path $root 'dotnet\src\LocalMorph.App\LocalMorph.App.csproj') `
            -f net10.0-windows10.0.19041.0 -c Release -p:WindowsOnly=true "-p:Platform=$architecture" "-p:RuntimeIdentifier=$rid" `
            -p:LocalMorphStoreBuild=true "-p:StoreMetadataRoot=$metadata" -p:SelfContained=true -p:WindowsAppSDKSelfContained=false `
            -p:PublishTrimmed=false -p:PublishAot=false -p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true `
            "-p:AppxPackageDir=$packageDirectory\" -p:AppxBundle=Never -p:UapAppxPackageBuildMode=StoreUpload `
            -p:AppxPackageSigningEnabled=false -p:AppxSymbolPackageEnabled=true -p:DebugSymbols=true -p:DebugType=portable `
            "-p:MsPdbCmfExeFullpath=$pdbTool" "-p:PdbCmfx64ExeFullPath=$pdbTool" "-p:PdbCmfx86ExeFullPath=$pdbTool" `
            "-p:ApplicationDisplayVersion=$($version.Substring(0, $version.LastIndexOf('.')))" -p:ApplicationVersion=0 -v:minimal
        if ($LASTEXITCODE -ne 0) { throw "Store build failed for $architecture." }
        $packages = @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File -Filter *.msix | Where-Object { $_.FullName -notmatch '\\Dependencies\\' })
        if ($packages.Count -ne 1) { throw "Expected one $architecture Store application MSIX." }
        Copy-Item -LiteralPath $packages[0].FullName -Destination (Join-Path $bundleInputs "LocalMorph-$rid.msix")
        $sdkUploads = @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File -Filter *.msixupload)
        if ($sdkUploads.Count -ne 1) { throw "Expected one SDK-generated $architecture upload containing crash symbols." }
        $sdkUpload = [IO.Compression.ZipFile]::OpenRead($sdkUploads[0].FullName)
        try {
            $symbols = @($sdkUpload.Entries | Where-Object { $_.Name -like '*.appxsym' })
            if ($symbols.Count -ne 1) { throw "Expected one $architecture symbol archive in the SDK upload." }
            $symbolPath = Join-Path $output "LocalMorph-$version-$rid.appxsym"
            [IO.Compression.ZipFileExtensions]::ExtractToFile($symbols[0], $symbolPath, $true)
            $symbolPackages += $symbolPath
        }
        finally { $sdkUpload.Dispose() }
    }
    $makeappx = Get-ChildItem -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Recurse -File -Filter makeappx.exe |
        Where-Object { $_.FullName -match '\\x64\\makeappx.exe\z' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $makeappx) { throw 'Windows SDK makeappx.exe was not found.' }
    $bundlePath = Join-Path $output "LocalMorph-$version.msixbundle"
    & $makeappx.FullName bundle /d $bundleInputs /p $bundlePath /bv $version /o
    if ($LASTEXITCODE -ne 0) { throw 'Store bundle creation failed.' }
    $upload = Join-Path $output "LocalMorph-$version.msixupload"
    Compress-Archive -LiteralPath (@($bundlePath) + $symbolPackages) -DestinationPath "$upload.zip" -Force
    Move-Item -LiteralPath "$upload.zip" -Destination $upload -Force
    & (Join-Path $PSScriptRoot 'Test-StoreUpload.ps1') -Upload $upload -IdentityName $IdentityName -Publisher $Publisher -Version $version -SourceCommit $sourceCommit
    Write-Host "Verified Store upload: $([IO.Path]::GetFileName($upload))"
}
finally {
    [IO.File]::WriteAllBytes($manifestPath, $originalManifest)
    [IO.Directory]::Delete($bundleInputs, $true)
}
