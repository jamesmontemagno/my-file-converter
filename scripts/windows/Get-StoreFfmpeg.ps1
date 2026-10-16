[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadRoot,
    [string]$ComplianceUrl = $env:FFMPEG_COMPLIANCE_URL,
    [string]$ComplianceSha256 = $env:FFMPEG_COMPLIANCE_SHA256
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Store-Functions.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$lockPath = Join-Path $root 'dotnet\packaging\ffmpeg-windows.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$uri = $null
if (-not [Uri]::TryCreate($ComplianceUrl, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $ComplianceSha256 -notmatch '\A[0-9a-fA-F]{64}\z') {
    throw 'Store packaging is blocked until FFMPEG_COMPLIANCE_URL (public HTTPS ZIP) and FFMPEG_COMPLIANCE_SHA256 identify an approved corresponding-source and notices archive for the pinned GPL binaries.'
}
$payload = [IO.Path]::GetFullPath($PayloadRoot)
New-Item -ItemType Directory -Path $payload -Force | Out-Null
$downloads = Join-Path $payload ('downloads-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $downloads | Out-Null
try {
    $compliance = Join-Path $downloads 'ffmpeg-corresponding-source.zip'
    Invoke-WebRequest -Uri $uri -OutFile $compliance
    Assert-FileHash $compliance $ComplianceSha256
    $archive = [IO.Compression.ZipFile]::OpenRead($compliance)
    try {
        $notice = $archive.GetEntry('NOTICE.txt')
        $manifest = $archive.GetEntry('SOURCE-MANIFEST.json')
        if (-not $notice -or -not $manifest -or -not ($archive.Entries | Where-Object { $_.FullName -like 'sources/*' -and $_.Length -gt 0 })) {
            throw 'Compliance archive must contain NOTICE.txt, SOURCE-MANIFEST.json, and corresponding source/build material under sources/.'
        }
        $reader = [IO.StreamReader]::new($manifest.Open())
        try { $sourceManifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        foreach ($rid in @('win-x64', 'win-arm64')) {
            if ($sourceManifest.binarySha256.$rid -ne $lock.$rid.sha256) { throw "Compliance archive does not describe the pinned $rid binary." }
        }
        $reader = [IO.StreamReader]::new($notice.Open())
        try { $noticeText = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ([string]::IsNullOrWhiteSpace($noticeText)) { throw 'FFmpeg redistribution notices are empty.' }
    }
    finally { $archive.Dispose() }
    foreach ($rid in @('win-x64', 'win-arm64')) {
        $pin = $lock.$rid
        $zip = Join-Path $downloads "$rid.zip"
        Invoke-WebRequest -Uri $pin.url -OutFile $zip
        Assert-FileHash $zip $pin.sha256
        $extracted = Join-Path $downloads $rid
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $extracted)
        $destination = Join-Path $payload $rid
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        foreach ($tool in @('ffmpeg.exe', 'ffprobe.exe')) {
            $files = @(Get-ChildItem -LiteralPath $extracted -Recurse -File -Filter $tool)
            if ($files.Count -ne 1) { throw "Expected exactly one $tool in the pinned $rid archive." }
            if ((Get-PeArchitecture $files[0].FullName) -ne $rid.Substring(4)) { throw "Wrong binary architecture for $rid / $tool." }
            Copy-Item -LiteralPath $files[0].FullName -Destination (Join-Path $destination $tool) -Force
        }
        $licenses = Join-Path $destination 'licenses'
        New-Item -ItemType Directory -Path $licenses -Force | Out-Null
        Copy-Item -LiteralPath $compliance -Destination (Join-Path $licenses 'ffmpeg-corresponding-source.zip') -Force
        [IO.File]::WriteAllText((Join-Path $licenses 'NOTICE.txt'), $noticeText)
        @{
            release = $lock.release; binaryUrl = $pin.url; binarySha256 = $pin.sha256
            correspondingSourceUrl = $ComplianceUrl; correspondingSourceSha256 = $ComplianceSha256
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $licenses 'provenance.json') -Encoding utf8
        foreach ($license in Get-ChildItem -LiteralPath $extracted -Recurse -File | Where-Object { $_.Name -match '\A(COPYING|LICENSE)(\..*)?\z' }) {
            Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $licenses $license.Name) -Force
        }
    }
}
finally { [IO.Directory]::Delete($downloads, $true) }
