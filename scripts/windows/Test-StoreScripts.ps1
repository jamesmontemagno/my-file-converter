$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Store-Functions.ps1')
function Assert-Equal($Actual, $Expected) { if ($Actual -ne $Expected) { throw "Expected '$Expected', got '$Actual'." } }
function New-TestPe([string]$Path, [string]$Architecture) {
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($Path))
    try {
        $writer.BaseStream.SetLength(128)
        $writer.Write([uint16]0x5A4D)
        $writer.BaseStream.Position = 0x3C
        $writer.Write([int32]64)
        $writer.BaseStream.Position = 64
        $writer.Write([uint32]0x00004550)
        $writer.Write([uint16]$(if ($Architecture -eq 'x64') { 0x8664 } else { 0xAA64 }))
    }
    finally { $writer.Dispose() }
}
function New-TestStoreUpload([string]$Directory, [string]$Scenario, [string]$Commit, [string]$Fixtures) {
    $bundle = Join-Path $Directory 'bundle'
    New-Item -ItemType Directory -Path (Join-Path $bundle 'AppxMetadata') -Force | Out-Null
    $packages = @()
    foreach ($architecture in @('x64', 'arm64')) {
        if ($Scenario -eq 'missing-architecture' -and $architecture -eq 'arm64') { continue }
        $package = Join-Path $Directory $architecture
        New-Item -ItemType Directory -Path $package -Force | Out-Null
        $identity = if ($Scenario -eq 'wrong-identity') { 'Wrong.Identity' } else { 'Test.StoreIdentity' }
        @"
<Package><Identity Name="$identity" Publisher="CN=TestPublisher" Version="1.2.3.0" ProcessorArchitecture="$architecture" />
<Dependencies><PackageDependency Name="Microsoft.WindowsAppRuntime.2.0" /></Dependencies>
<Applications><Verb Clsid="761079A6-3C8C-4781-AF52-FB07D1C43C0A" /></Applications></Package>
"@ | Set-Content -LiteralPath (Join-Path $package 'AppxManifest.xml')
        $machine = if ($Scenario -eq 'wrong-runtime') { 'x64' } else { $architecture }
        New-TestPe (Join-Path $package 'coreclr.dll') $machine
        foreach ($module in @('LocalMorph.App', 'LocalMorph.Core', 'LocalMorph.Shell')) {
            $fixtureArchitecture = if ($Scenario -eq 'wrong-shell' -and $module -eq 'LocalMorph.Shell') { 'x64' } else { $architecture }
            Copy-Item -LiteralPath (Join-Path $Fixtures "$fixtureArchitecture\$module.dll") -Destination $package
        }
        if ($Scenario -eq 'installed-pdb') { Copy-Item -LiteralPath (Join-Path $Fixtures "$architecture\LocalMorph.App.pdb") -Destination $package }
        if ($Scenario -in @('bundled-ffmpeg', 'bundled-ffprobe', 'nested-ffmpeg')) {
            $toolDirectory = if ($Scenario -eq 'nested-ffmpeg') { Join-Path $package 'Resources\ffmpeg\win-x64\bin' } else { Join-Path $package 'tools' }
            New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
            $tool = if ($Scenario -eq 'bundled-ffprobe') { 'ffprobe.exe' } else { 'ffmpeg.exe' }
            New-TestPe (Join-Path $toolDirectory $tool) $architecture
        }
        $applicationCommit = if ($Scenario -eq 'wrong-commit') { '0' * 40 } else { $Commit }
        $applicationVersion = if ($Scenario -eq 'wrong-build-version') { '1.2.4.0' } else { '1.2.3.0' }
        $runtimeIdentifier = if ($Scenario -eq 'wrong-build-rid') { 'win-other' } else { "win-$architecture" }
        $mediaTools = if ($Scenario -eq 'wrong-tool-mode') { 'bundled' } else { 'external' }
        if ($Scenario -ne 'missing-metadata') {
            @{ sourceCommit = $applicationCommit; version = $applicationVersion; runtimeIdentifier = $runtimeIdentifier; mediaTools = $mediaTools } |
                ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'localmorph-build.json')
        }
        if ($Scenario -eq 'signed') { 'Not a real signature.' | Set-Content -LiteralPath (Join-Path $package 'AppxSignature.p7x') }
        [IO.Compression.ZipFile]::CreateFromDirectory($package, (Join-Path $bundle "$architecture.msix"))
        $packages += "<Package Type=`"application`" Architecture=`"$architecture`" FileName=`"$architecture.msix`" />"
    }
    $bundleIdentity = if ($Scenario -eq 'wrong-bundle') { 'Wrong.Bundle' } else { 'Test.StoreIdentity' }
    "<Bundle><Identity Name=`"$bundleIdentity`" Publisher=`"CN=TestPublisher`" Version=`"1.2.3.0`" /><Packages>$($packages -join '')</Packages></Bundle>" |
        Set-Content -LiteralPath (Join-Path $bundle 'AppxMetadata\AppxBundleManifest.xml')
    $upload = Join-Path $Directory 'upload'
    New-Item -ItemType Directory -Path $upload | Out-Null
    [IO.Compression.ZipFile]::CreateFromDirectory($bundle, (Join-Path $upload 'test.msixbundle'))
    foreach ($architecture in @('x64', 'arm64')) {
        if ($Scenario -eq 'no-symbols' -or ($Scenario -eq 'missing-architecture-symbols' -and $architecture -eq 'arm64')) { continue }
        $symbols = Join-Path $Directory "symbols-$architecture"
        New-Item -ItemType Directory -Path $symbols | Out-Null
        if ($Scenario -ne 'empty-symbols') {
            Copy-Item -LiteralPath (Join-Path $Fixtures "$architecture\LocalMorph.App.pdb"), (Join-Path $Fixtures "$architecture\LocalMorph.Core.pdb"), (Join-Path $Fixtures "$architecture\LocalMorph.Shell.pdb") -Destination $symbols
        }
        switch ($Scenario) {
            'missing-app-pdb' { Remove-Item -LiteralPath (Join-Path $symbols 'LocalMorph.App.pdb') }
            'missing-core-pdb' { Remove-Item -LiteralPath (Join-Path $symbols 'LocalMorph.Core.pdb') }
            'missing-shell-pdb' { Remove-Item -LiteralPath (Join-Path $symbols 'LocalMorph.Shell.pdb') }
            'empty-pdb' { [IO.File]::WriteAllBytes((Join-Path $symbols 'LocalMorph.App.pdb'), [byte[]]@()) }
            'invalid-pdb' { 'Invalid PDB fixture.' | Set-Content -LiteralPath (Join-Path $symbols 'LocalMorph.App.pdb') }
            'wrong-managed-symbols' {
                $other = if ($architecture -eq 'x64') { 'arm64' } else { 'x64' }
                Copy-Item -LiteralPath (Join-Path $Fixtures "$other\LocalMorph.App.pdb") -Destination $symbols -Force
            }
            'wrong-shell-symbols' {
                $other = if ($architecture -eq 'x64') { 'arm64' } else { 'x64' }
                Copy-Item -LiteralPath (Join-Path $Fixtures "$other\LocalMorph.Shell.pdb") -Destination $symbols -Force
            }
            'duplicate-pdb' {
                $duplicate = Join-Path $symbols 'duplicate'
                New-Item -ItemType Directory -Path $duplicate | Out-Null
                Copy-Item -LiteralPath (Join-Path $symbols 'LocalMorph.App.pdb') -Destination $duplicate
            }
        }
        $symbolVersion = if ($Scenario -eq 'wrong-symbol-version') { '1.2.2.0' } else { '1.2.3.0' }
        $symbolPackage = Join-Path $upload "LocalMorph-$symbolVersion-win-$architecture.appxsym"
        if ($Scenario -eq 'invalid-symbol-archive') { 'Invalid ZIP fixture.' | Set-Content -LiteralPath $symbolPackage }
        else { [IO.Compression.ZipFile]::CreateFromDirectory($symbols, $symbolPackage) }
    }
    $path = Join-Path $Directory 'test.msixupload'
    [IO.Compression.ZipFile]::CreateFromDirectory($upload, $path)
    return $path
}
foreach ($tag in @('v1.2.3-windows', 'v1.2.3.0-windows')) { Assert-Equal (Get-StoreVersion $tag) '1.2.3.0' }
foreach ($tag in @('v1.2.3.4-windows', 'v0.2.3-windows', 'v65536.1.2-windows', 'main', "v1.2.3-windows`n")) {
    $rejected = $false
    try { Get-StoreVersion $tag | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid Store tag accepted: $tag" }
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('localmorph-store-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $windows = Join-Path $root 'dotnet\src\LocalMorph.App\Platforms\Windows'
    $destination = Join-Path $temporary 'AppxManifest.xml'
    $original = (Get-FileHash -LiteralPath (Join-Path $windows 'Package.appxmanifest')).Hash
    New-StoreManifest -DirectPath (Join-Path $windows 'Package.appxmanifest') -TemplatePath (Join-Path $windows 'Package.Store.appxmanifest') -DestinationPath $destination -Name 'Test.StoreIdentity' -Publisher 'CN=TestPublisher' -PublisherDisplayName 'Test Publisher' -Version '1.2.3.0'
    [xml]$manifest = Get-Content -LiteralPath $destination -Raw
    Assert-Equal $manifest.Package.Identity.Name 'Test.StoreIdentity'
    Assert-Equal $manifest.Package.Identity.Version '1.2.3.0'
    Assert-Equal (Get-FileHash -LiteralPath (Join-Path $windows 'Package.appxmanifest')).Hash $original
    Assert-Equal $manifest.SelectNodes("//*[local-name()='Verb' and @Clsid='761079A6-3C8C-4781-AF52-FB07D1C43C0A']").Count 1
    foreach ($extension in @('.srt', '.vtt', '.ass', '.ssa')) {
        Assert-Equal $manifest.SelectNodes("//*[local-name()='FileType' and text()='$extension']").Count 1
    }
    foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter *.ps1) {
        $errors = $null
        [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$null, [ref]$errors) | Out-Null
        if ($errors.Count) { throw "PowerShell syntax errors in $($script.Name): $errors" }
    }
    $shell = Join-Path $root 'artifacts\native-shell\x64\LocalMorph.Shell.dll'
    if (Test-Path -LiteralPath $shell) { Assert-Equal (Get-PeArchitecture $shell) 'x64' }
    $fixtures = Join-Path $temporary 'symbol-fixtures'
    foreach ($architecture in @('x64', 'arm64')) {
        foreach ($module in @('LocalMorph.App', 'LocalMorph.Core', 'LocalMorph.Shell')) {
            $fixtureProject = Join-Path $fixtures "$architecture\$module\Fixture.csproj"
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($fixtureProject)) -Force | Out-Null
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><DebugSymbols>true</DebugSymbols><DebugType>portable</DebugType></PropertyGroup></Project>' |
                Set-Content -LiteralPath $fixtureProject
            'public static class SymbolFixture { public static int Value => 1; }' |
                Set-Content -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($fixtureProject)) 'SymbolFixture.cs')
            $buildOutput = & dotnet build $fixtureProject -c Release "-p:AssemblyName=$module" "-p:PlatformTarget=$architecture" --output (Join-Path $fixtures $architecture) -v:quiet 2>&1
            if ($LASTEXITCODE -ne 0) { throw "Symbol fixture build failed: $buildOutput" }
            if (-not (Test-Path -LiteralPath (Join-Path $fixtures "$architecture\$module.dll")) -or -not (Test-Path -LiteralPath (Join-Path $fixtures "$architecture\$module.pdb"))) {
                throw "Symbol fixture build did not produce $architecture $module DLL/PDB: $buildOutput"
            }
        }
    }
    $symbolFailures = [ordered]@{
        'no-symbols' = '*must contain x64 and ARM64 crash-symbol archives*'
        'missing-architecture-symbols' = '*must contain x64 and ARM64 crash-symbol archives*'
        'wrong-symbol-version' = '*Missing * crash-symbol archive for version*'
        'empty-symbols' = '*Expected exactly one * LocalMorph.App PDB*'
        'missing-app-pdb' = '*Expected exactly one * LocalMorph.App PDB*'
        'missing-core-pdb' = '*Expected exactly one * LocalMorph.Core PDB*'
        'missing-shell-pdb' = '*Expected exactly one * LocalMorph.Shell PDB*'
        'empty-pdb' = '*Empty or truncated symbols*'
        'invalid-pdb' = '*Cannot read native symbols*'
        'wrong-managed-symbols' = '*Portable symbols do not match LocalMorph.App.dll*'
        'wrong-shell-symbols' = '*Portable symbols do not match LocalMorph.Shell.dll*'
        'duplicate-pdb' = '*Expected exactly one * LocalMorph.App PDB*'
        'invalid-symbol-archive' = '*Central Directory*'
        'installed-pdb' = '*not the installed application*'
    }
    $commit = '1' * 40
    $packageFailures = [ordered]@{
        'missing-architecture' = '*exactly x64 and ARM64 application packages*'
        'wrong-identity' = '*Unexpected * Store identity*'
        'wrong-bundle' = '*Unexpected Store bundle identity*'
        'wrong-runtime' = '*not .NET self-contained*'
        'wrong-shell' = '*LocalMorph.Shell.dll has the wrong architecture*'
        'missing-metadata' = '*Missing Store application provenance*'
        'wrong-commit' = '*provenance does not match*'
        'wrong-build-version' = '*provenance does not match*'
        'wrong-build-rid' = '*provenance does not match*'
        'wrong-tool-mode' = '*must declare external media tools*'
        'bundled-ffmpeg' = '*must not bundle FFmpeg or FFprobe*'
        'bundled-ffprobe' = '*must not bundle FFmpeg or FFprobe*'
        'nested-ffmpeg' = '*must not bundle FFmpeg or FFprobe*'
        'signed' = '*direct-release signature*'
    }
    foreach ($scenario in (@('valid') + @($packageFailures.Keys) + @($symbolFailures.Keys))) {
        $upload = New-TestStoreUpload (Join-Path $temporary $scenario) $scenario $commit $fixtures
        $failure = $null
        try {
            & (Join-Path $PSScriptRoot 'Test-StoreUpload.ps1') -Upload $upload -IdentityName 'Test.StoreIdentity' -Publisher 'CN=TestPublisher' -Version '1.2.3.0' -SourceCommit $commit
        }
        catch { $failure = $_.Exception.Message }
        if ($scenario -eq 'valid' -and $failure) { throw "Valid synthetic upload rejected: $failure" }
        if ($scenario -ne 'valid' -and -not $failure) { throw "Invalid synthetic upload accepted: $scenario" }
        if ($packageFailures.Contains($scenario) -and $failure -notlike $packageFailures[$scenario]) { throw "Unexpected $scenario rejection: $failure" }
        if ($symbolFailures.Contains($scenario) -and $failure -notlike $symbolFailures[$scenario]) { throw "Unexpected $scenario rejection: $failure" }
        Write-Host "Store artifact scenario passed: $scenario"
    }
    $originalToken = $env:GH_TOKEN
    $originalRepository = $env:GITHUB_REPOSITORY
    try {
        $env:GH_TOKEN = 'local-test-not-a-token'
        $env:GITHUB_REPOSITORY = 'local-test/repo'
        function gh { $global:LASTEXITCODE = 0; return $global:LocalMorphApprovalTestResponse }
        $gate = @{ can_admins_bypass = $false; protection_rules = @(@{ type = 'required_reviewers'; prevent_self_review = $true; reviewers = @(@{ reviewer = @{ id = 1 } }) }) }
        $global:LocalMorphApprovalTestResponse = $gate | ConvertTo-Json -Depth 6
        & (Join-Path $PSScriptRoot 'Test-StoreApproval.ps1')
        foreach ($scenario in @('no-reviewers', 'self-review', 'admin-bypass')) {
            $invalid = $global:LocalMorphApprovalTestResponse | ConvertFrom-Json
            switch ($scenario) {
                'no-reviewers' { $invalid.protection_rules[0].reviewers = @() }
                'self-review' { $invalid.protection_rules[0].prevent_self_review = $false }
                'admin-bypass' { $invalid.can_admins_bypass = $true }
            }
            $global:LocalMorphApprovalTestResponse = $invalid | ConvertTo-Json -Depth 6
            $rejected = $false
            try { & (Join-Path $PSScriptRoot 'Test-StoreApproval.ps1') } catch { $rejected = $true }
            if (-not $rejected) { throw "Unsafe approval configuration accepted: $scenario" }
            $global:LocalMorphApprovalTestResponse = $gate | ConvertTo-Json -Depth 6
        }
    }
    finally {
        Remove-Item Function:\gh
        Remove-Variable -Name LocalMorphApprovalTestResponse -Scope Global
        $env:GH_TOKEN = $originalToken
        $env:GITHUB_REPOSITORY = $originalRepository
    }
    Write-Host "Store version, identity, activation, provenance, approval, and $(1 + $packageFailures.Count + $symbolFailures.Count) artifact scenarios passed, including external media tools, matching crash symbols and symbol rejection paths. Synthetic packages do not replace real SDK packaging, native symbol generation, WACK, or installation tests."
}
finally { [IO.Directory]::Delete($temporary, $true) }
