[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Version,

    [switch]$Mac,
    [switch]$Windows,
    [switch]$Push
)

# Creates the per-platform release tags that trigger .github/workflows/mac-release.yml and
# windows-release.yml. Example: .\scripts\create-release-tags.ps1 v1.0.0 -Windows -Mac -Push

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Version -notmatch '^v\d+\.\d+\.\d+(\.\d+)?$') {
    throw "Version must match v<major>.<minor>.<patch> or v<major>.<minor>.<patch>.<revision> (example: v1.0.0 or v1.0.0.1)."
}
if (-not $Mac -and -not $Windows) {
    throw "Select at least one platform with -Mac and/or -Windows."
}

$tags = @()
if ($Mac) { $tags += "$Version-mac" }
if ($Windows) { $tags += "$Version-windows" }

git rev-parse --is-inside-work-tree *> $null
if ($LASTEXITCODE -ne 0) { throw "This script must run inside a git repository." }

foreach ($tag in $tags) {
    git rev-parse -q --verify "refs/tags/$tag" *> $null
    if ($LASTEXITCODE -eq 0) { throw "Tag '$tag' already exists locally." }
    if (-not [string]::IsNullOrWhiteSpace((git ls-remote --tags origin "refs/tags/$tag" 2>$null))) { throw "Tag '$tag' already exists on origin." }
}

$changelog = Join-Path $PSScriptRoot "..\dotnet\CHANGELOG.md"
if (Test-Path $changelog) {
    foreach ($tag in $tags) {
        if (-not (Select-String -Path $changelog -Pattern "^## \[?$([regex]::Escape($tag))" -Quiet)) {
            Write-Warning "dotnet/CHANGELOG.md has no '## $tag' section; release notes will fall back to a generic line."
        }
    }
}

foreach ($tag in $tags) {
    if ($PSCmdlet.ShouldProcess($tag, "Create annotated tag")) { git tag -a $tag -m "Release $tag" }
}

if ($Push -and $PSCmdlet.ShouldProcess("origin", "Push tags $($tags -join ' ')")) {
    git push origin @tags
}

Write-Host "Created tags:"
$tags | ForEach-Object { Write-Host "- $_" }
if (-not $Push) { Write-Host ("Push with: git push origin {0}" -f ($tags -join " ")) }
