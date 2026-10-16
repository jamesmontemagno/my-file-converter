[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('x64', 'ARM64')][string]$Architecture,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio Build Tools with Desktop development with C++ and the x64/ARM64 MSVC tools.' }
$components = @('Microsoft.VisualStudio.Component.VC.Tools.x86.x64')
if ($Architecture -eq 'ARM64') { $components += 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' }
$msbuild = & $vswhere -latest -products '*' -requires $components -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual C++ build tools were not found. Install Desktop development with C++ and ARM64 tools for ARM64 packaging.' }
$project = Join-Path $PSScriptRoot '..\..\dotnet\src\LocalMorph.Shell\LocalMorph.Shell.vcxproj'
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\') + '\'
& $msbuild $project /nologo /m:1 /v:minimal /p:Configuration=Release "/p:Platform=$Architecture" "/p:OutDir=$output"
if ($LASTEXITCODE -ne 0) { throw "Explorer command build failed for $Architecture." }
if (-not (Test-Path -LiteralPath (Join-Path $output 'LocalMorph.Shell.dll'))) { throw 'Explorer command DLL was not produced.' }
