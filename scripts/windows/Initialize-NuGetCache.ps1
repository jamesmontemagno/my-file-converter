$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    Write-Host 'NuGet cache relocation is only enabled on GitHub-hosted Windows.'
    return
}
if (-not $env:RUNNER_TEMP -or -not $env:GITHUB_ENV) {
    throw 'Hosted NuGet cache setup requires RUNNER_TEMP and GITHUB_ENV.'
}
foreach ($cache in @(
    @{ Name = 'NUGET_PACKAGES'; Directory = 'nuget\packages' },
    @{ Name = 'NUGET_HTTP_CACHE_PATH'; Directory = 'nuget\http-cache' }
)) {
    $existing = [Environment]::GetEnvironmentVariable($cache.Name)
    if ($existing) {
        Write-Host "Preserving explicit $($cache.Name)=$existing"
        continue
    }
    $path = Join-Path $env:RUNNER_TEMP $cache.Directory
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    "$($cache.Name)=$path" >> $env:GITHUB_ENV
    Write-Host "$($cache.Name)=$path"
}
