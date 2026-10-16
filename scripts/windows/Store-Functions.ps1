function Get-StoreVersion {
    param([Parameter(Mandatory)][string]$Tag)
    if ($Tag -cnotmatch '\Av(?<base>\d+\.\d+\.\d+)(?:\.0)?-windows\z') {
        throw 'Store tags must be vX.Y.Z-windows or vX.Y.Z.0-windows. Store reserves the fourth component as zero.'
    }
    $version = [version]"$($Matches.base).0"
    if ($version.Major -lt 1 -or $version.Major -gt 65535 -or $version.Minor -gt 65535 -or $version.Build -gt 65535) {
        throw 'Store version components must be 0..65535, and the major component must be nonzero.'
    }
    return $version.ToString(4)
}

function Get-PeArchitecture {
    param([Parameter(Mandatory)][string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Not a PE binary: $Path" }
        $stream.Position = 0x3C
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt $stream.Length - 6) { throw "Invalid PE header: $Path" }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw "Invalid PE signature: $Path" }
        switch ($reader.ReadUInt16()) {
            0x8664 { return 'x64' }
            0xAA64 { return 'arm64' }
            default { throw "Unsupported PE machine type: $Path" }
        }
    }
    finally { $reader.Dispose() }
}

function Assert-FileHash {
    param([string]$Path, [string]$Sha256)
    if ($Sha256 -notmatch '\A[0-9a-fA-F]{64}\z' -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256) {
        throw "SHA256 verification failed: $([IO.Path]::GetFileName($Path))"
    }
}

function Assert-PdbMatchesBinary {
    param([Parameter(Mandatory)][string]$Binary, [Parameter(Mandatory)][string]$Pdb)
    if (-not ('LocalMorph.Packaging.StoreSymbols' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'Store-Symbols.cs')
    }
    [LocalMorph.Packaging.StoreSymbols]::AssertMatch($Binary, $Pdb)
}

function New-StoreManifest {
    param([string]$DirectPath, [string]$TemplatePath, [string]$DestinationPath, [string]$Name, [string]$Publisher, [string]$PublisherDisplayName, [string]$Version)
    foreach ($value in @($Name, $Publisher, $PublisherDisplayName)) {
        if ([string]::IsNullOrWhiteSpace($value)) { throw 'Store identity name, publisher, and publisher display name are required.' }
    }
    [xml]$direct = Get-Content -LiteralPath $DirectPath -Raw
    [xml]$store = Get-Content -LiteralPath $TemplatePath -Raw
    if ($Name -eq $direct.Package.Identity.Name -and $Publisher -eq $direct.Package.Identity.Publisher) { throw 'Store identity must not silently reuse the direct-release package identity.' }
    $store.Package.Identity.Name = $Name
    $store.Package.Identity.Publisher = $Publisher
    $store.Package.Identity.Version = $Version
    $store.Package.Properties.PublisherDisplayName = $PublisherDisplayName
    $extensions = $direct.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']/*[local-name()='Extensions']")
    if (-not $extensions) { throw 'Shared Windows activation registrations are missing.' }
    $application = $store.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']")
    [void]$application.AppendChild($store.ImportNode($extensions, $true))
    $store.Save($DestinationPath)
}
