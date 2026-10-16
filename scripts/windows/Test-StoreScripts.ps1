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
function New-TestStoreUpload([string]$Directory, [string]$Scenario, [string]$Commit) {
    $bundle = Join-Path $Directory 'bundle'
    New-Item -ItemType Directory -Path (Join-Path $bundle 'AppxMetadata') -Force | Out-Null
    $packages = @()
    $pins = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\dotnet\packaging\ffmpeg-windows.lock.json') -Raw | ConvertFrom-Json
    foreach ($architecture in @('x64', 'arm64')) {
        if ($Scenario -eq 'missing-architecture' -and $architecture -eq 'arm64') { continue }
        $package = Join-Path $Directory $architecture
        $licenses = Join-Path $package 'tools\licenses'
        New-Item -ItemType Directory -Path $licenses -Force | Out-Null
        $identity = if ($Scenario -eq 'wrong-identity') { 'Wrong.Identity' } else { 'Test.StoreIdentity' }
        @"
<Package><Identity Name="$identity" Publisher="CN=TestPublisher" Version="1.2.3.0" ProcessorArchitecture="$architecture" />
<Dependencies><PackageDependency Name="Microsoft.WindowsAppRuntime.2.0" /></Dependencies>
<Applications><Verb Clsid="761079A6-3C8C-4781-AF52-FB07D1C43C0A" /></Applications></Package>
"@ | Set-Content -LiteralPath (Join-Path $package 'AppxManifest.xml')
        foreach ($relative in @('coreclr.dll', 'tools\ffmpeg.exe', 'tools\ffprobe.exe', 'LocalMorph.Shell.dll')) {
            $machine = if (($Scenario -eq 'wrong-runtime' -and $relative -eq 'coreclr.dll') -or ($Scenario -eq 'wrong-tool' -and $relative -eq 'tools\ffprobe.exe')) { 'x64' } else { $architecture }
            New-TestPe (Join-Path $package $relative) $machine
        }
        if ($Scenario -ne 'missing-notices') { 'Synthetic fixture, not redistribution material.' | Set-Content -LiteralPath (Join-Path $licenses 'NOTICE.txt') }
        'Synthetic fixture, not a corresponding-source distribution.' | Set-Content -LiteralPath (Join-Path $licenses 'ffmpeg-corresponding-source.zip')
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $licenses 'ffmpeg-corresponding-source.zip')).Hash
        if ($Scenario -eq 'source-hash') { $sourceHash = '0' * 64 }
        $pin = $pins.("win-$architecture")
        @{ binarySha256 = $pin.sha256; binaryUrl = $pin.url; correspondingSourceSha256 = $sourceHash } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $licenses 'provenance.json')
        $applicationCommit = if ($Scenario -eq 'wrong-commit') { '0' * 40 } else { $Commit }
        @{ sourceCommit = $applicationCommit; version = '1.2.3.0'; runtimeIdentifier = "win-$architecture" } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $licenses 'localmorph-build.json')
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
    $commit = '1' * 40
    foreach ($scenario in @('valid', 'missing-architecture', 'wrong-identity', 'wrong-bundle', 'wrong-runtime', 'wrong-tool', 'missing-notices', 'wrong-commit', 'source-hash', 'signed')) {
        $upload = New-TestStoreUpload (Join-Path $temporary $scenario) $scenario $commit
        $failure = $null
        try {
            & (Join-Path $PSScriptRoot 'Test-StoreUpload.ps1') -Upload $upload -IdentityName 'Test.StoreIdentity' -Publisher 'CN=TestPublisher' -Version '1.2.3.0' -SourceCommit $commit
        }
        catch { $failure = $_.Exception.Message }
        if ($scenario -eq 'valid' -and $failure) { throw "Valid synthetic upload rejected: $failure" }
        if ($scenario -ne 'valid' -and -not $failure) { throw "Invalid synthetic upload accepted: $scenario" }
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
    Write-Host 'Store version, identity, activation, provenance, approval, and synthetic artifact failure-path tests passed. Synthetic fixtures do not replace real SDK packaging, WACK, or installation tests.'
}
finally { [IO.Directory]::Delete($temporary, $true) }
