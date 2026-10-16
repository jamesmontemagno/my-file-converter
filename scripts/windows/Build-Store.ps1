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
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sourceCommit = git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the Store application source commit.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$payload = Join-Path $output 'payload'
& (Join-Path $PSScriptRoot 'Get-StoreFfmpeg.ps1') -PayloadRoot $payload
foreach ($rid in @('win-x64', 'win-arm64')) {
    @{ sourceCommit = $sourceCommit; version = $version; runtimeIdentifier = $rid } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload "$rid\licenses\localmorph-build.json") -Encoding utf8
}
$manifestPath = Join-Path $root 'dotnet\src\LocalMorph.App\Platforms\Windows\Package.appxmanifest'
$originalManifest = [IO.File]::ReadAllBytes($manifestPath)
$bundleInputs = Join-Path $output ('bundle-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $bundleInputs | Out-Null
try {
    New-StoreManifest -DirectPath $manifestPath -TemplatePath (Join-Path ([IO.Path]::GetDirectoryName($manifestPath)) 'Package.Store.appxmanifest') -DestinationPath $manifestPath -Name $IdentityName -Publisher $Publisher -PublisherDisplayName $PublisherDisplayName -Version $version
    foreach ($architecture in @('x64', 'ARM64')) {
        $rid = 'win-' + $architecture.ToLowerInvariant()
        $packageDirectory = Join-Path $output ('package-' + $rid + '-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $packageDirectory | Out-Null
        dotnet publish (Join-Path $root 'dotnet\src\LocalMorph.App\LocalMorph.App.csproj') `
            -f net10.0-windows10.0.19041.0 -c Release -p:WindowsOnly=true "-p:Platform=$architecture" "-p:RuntimeIdentifier=$rid" `
            -p:LocalMorphStoreBuild=true "-p:StorePayloadRoot=$payload" -p:SelfContained=true -p:WindowsAppSDKSelfContained=false `
            -p:PublishTrimmed=false -p:PublishAot=false -p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true `
            "-p:AppxPackageDir=$packageDirectory\" -p:AppxBundle=Never -p:UapAppxPackageBuildMode=StoreUpload `
            -p:AppxPackageSigningEnabled=false -p:AppxSymbolPackageEnabled=false `
            "-p:ApplicationDisplayVersion=$($version.Substring(0, $version.LastIndexOf('.')))" -p:ApplicationVersion=0 -v:minimal
        if ($LASTEXITCODE -ne 0) { throw "Store build failed for $architecture." }
        $packages = @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File -Filter *.msix | Where-Object { $_.FullName -notmatch '\\Dependencies\\' })
        if ($packages.Count -ne 1) { throw "Expected one $architecture Store application MSIX." }
        Copy-Item -LiteralPath $packages[0].FullName -Destination (Join-Path $bundleInputs "LocalMorph-$rid.msix")
    }
    $makeappx = Get-ChildItem -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Recurse -File -Filter makeappx.exe |
        Where-Object { $_.FullName -match '\\x64\\makeappx.exe\z' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $makeappx) { throw 'Windows SDK makeappx.exe was not found.' }
    $bundlePath = Join-Path $output "LocalMorph-$version.msixbundle"
    & $makeappx.FullName bundle /d $bundleInputs /p $bundlePath /bv $version /o
    if ($LASTEXITCODE -ne 0) { throw 'Store bundle creation failed.' }
    $upload = Join-Path $output "LocalMorph-$version.msixupload"
    Compress-Archive -LiteralPath $bundlePath -DestinationPath "$upload.zip" -Force
    Move-Item -LiteralPath "$upload.zip" -Destination $upload -Force
    & (Join-Path $PSScriptRoot 'Test-StoreUpload.ps1') -Upload $upload -IdentityName $IdentityName -Publisher $Publisher -Version $version -SourceCommit $sourceCommit
    Write-Host "Verified Store upload: $([IO.Path]::GetFileName($upload))"
}
finally {
    [IO.File]::WriteAllBytes($manifestPath, $originalManifest)
    [IO.Directory]::Delete($bundleInputs, $true)
}
