param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$DemoDirectory,
    [ValidateSet('Start','Join','Images')][string]$ResumeAt = 'Start'
)
$ErrorActionPreference = 'Stop'
$kit = Split-Path $PSScriptRoot
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$project = Join-Path $repository 'dotnet\src\LocalMorph.App\LocalMorph.App.csproj'
function Invoke-UI([string[]]$Arguments) {
    $output = & winapp ui @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($output -join "`n") }
    return $output -join "`n"
}
function Wait-Ready {
    Invoke-UI @('wait-for', 'ConvertButton', '-a', "$AppPid", '-p', 'IsEnabled', '--value', 'True', '-t', '20000') | Out-Null
}
function Import-Demo([string[]]$Names) {
    $arguments = ($Names | ForEach-Object { '"' + (Join-Path $DemoDirectory $_) + '"' }) -join ' '
    & winapp run $project -c Release --arch x64 -f net10.0-windows10.0.19041.0 -p WindowsOnly=true --no-build --no-restore --args $arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Demo file activation failed.' }
    Invoke-UI @('wait-for', 'FilesSummary', '-a', "$AppPid", '--value', "$($Names.Count) file", '--contains', '-t', '15000') | Out-Null
    Wait-Ready
}
function Set-Operation([string]$Label) {
    Invoke-UI @('invoke', 'OperationPicker', '-a', "$AppPid") | Out-Null
    Start-Sleep -Milliseconds 250
    $windows = Invoke-UI @('list-windows', '-a', "$AppPid", '--json') | ConvertFrom-Json
    $popup = @($windows | Where-Object { $_.title -eq 'PopupHost' })
    if ($popup.Count -gt 1) { throw 'Ambiguous task popup windows.' }
    if ($popup.Count -eq 1) {
        Invoke-UI @('invoke', $Label, '-w', "$($popup[0].hwnd)") | Out-Null
    } else {
        Invoke-UI @('invoke', $Label, '-a', "$AppPid") | Out-Null
    }
    Invoke-UI @('wait-for', 'OperationPicker', '-a', "$AppPid", '--value', $Label, '-t', '3000') | Out-Null
}
function Capture([string]$Name) {
    Start-Sleep -Milliseconds 500
    Invoke-UI @('screenshot', '-w', "$script:mainHwnd", '-o', (Join-Path $kit "en-us\screenshots\$Name")) | Out-Null
}
function Scroll-SettingsTop {
    $properties = (Invoke-UI @('get-property', 'SettingsColumn', '-a', "$AppPid", '--json') | ConvertFrom-Json).properties
    if ($null -eq $properties.ScrollVerticalPercent) { throw 'Settings did not expose its scroll position.' }
    if ([double]$properties.ScrollVerticalPercent -ge 0) {
        Invoke-UI @('scroll', 'SettingsColumn', '-a', "$AppPid", '--to', 'top') | Out-Null
    }
}
function Convert-Demo {
    Invoke-UI @('invoke', 'ConvertButton', '-a', "$AppPid") | Out-Null
    Invoke-UI @('wait-for', 'CancelAllButton', '-a', "$AppPid", '--gone', '-t', '120000') | Out-Null
    $status = (Invoke-UI @('get-value', 'BatchStatus', '-a', "$AppPid", '--json') | ConvertFrom-Json).text
    if ($status -match '[1-9][0-9]* failed|[1-9][0-9]* canceled' -or $status -notmatch '[1-9][0-9]* completed') {
        throw "Demo conversion did not succeed: $status"
    }
}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class StoreCaptureWindow {
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    public static bool Resize(IntPtr hwnd) {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { return SetWindowPos(hwnd, IntPtr.Zero, 20, 20, 2576, 1448, 0x14); }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
}
'@
$status = Invoke-UI @('status', '-a', "$AppPid", '--json') | ConvertFrom-Json
$script:mainHwnd = $status.hwnd
if (-not [StoreCaptureWindow]::Resize([IntPtr]$status.hwnd)) { throw 'Could not size the owned capture window.' }
Wait-Ready
$videos = @('Coastal walk.mov', 'Headland sunset.mov', 'Ocean overlook.mov')
if ($ResumeAt -ne 'Images') {
if ($ResumeAt -eq 'Start') {
Invoke-UI @('invoke', 'Select Coastal walk.mov', '-a', "$AppPid") | Out-Null
Invoke-UI @('invoke', 'Apply preset Share anywhere', '-a', "$AppPid") | Out-Null
Scroll-SettingsTop
Capture '01-batch-conversion.png'
Invoke-UI @('invoke', 'Apply preset Fit in 25 MB', '-a', "$AppPid") | Out-Null
Invoke-UI @('scroll-into-view', 'TargetSizeEntry', '-a', "$AppPid") | Out-Null
Capture '02-sharing-presets.png'
Invoke-UI @('invoke', 'Apply preset Share anywhere', '-a', "$AppPid") | Out-Null
Invoke-UI @('invoke', 'PreviewSizeButton', '-a', "$AppPid") | Out-Null
Invoke-UI @('set-value', 'TrimStartSlider', '1', '-a', "$AppPid") | Out-Null
Invoke-UI @('set-value', 'TrimEndSlider', '10', '-a', "$AppPid") | Out-Null
Invoke-UI @('scroll', 'PreviewInspector', '-a', "$AppPid", '--to', 'bottom') | Out-Null
Invoke-UI @('scroll-into-view', 'KeepAllAudioSwitch', '-a', "$AppPid") | Out-Null
Capture '03-preview-trim-tracks.png'
Convert-Demo
Invoke-UI @('invoke', 'ClearAllButton', '-a', "$AppPid") | Out-Null
Import-Demo $videos
Invoke-UI @('invoke', 'PreviewSizeButton', '-a', "$AppPid") | Out-Null
Set-Operation 'Join compatible clips'
}
Invoke-UI @('wait-for', 'OperationPicker', '-a', "$AppPid", '--value', 'Join compatible clips', '-t', '3000') | Out-Null
Wait-Ready
Scroll-SettingsTop
Capture '04-join-compatible-clips.png'
Convert-Demo
Invoke-UI @('invoke', 'ClearAllButton', '-a', "$AppPid") | Out-Null
Set-Operation 'Convert files'
Import-Demo @('Coastal walk.mov')
Invoke-UI @('invoke', 'Apply preset Share anywhere', '-a', "$AppPid") | Out-Null
Set-Operation 'Split into clips'
Invoke-UI @('set-value', 'SegmentDurationEntry', '4', '-a', "$AppPid") | Out-Null
Invoke-UI @('focus', 'OperationPicker', '-a', "$AppPid") | Out-Null
Wait-Ready
Capture '05-split-into-clips.png'
Convert-Demo
Invoke-UI @('invoke', 'ClearAllButton', '-a', "$AppPid") | Out-Null
Set-Operation 'Convert files'
Import-Demo @('Coast at golden hour.jpg', 'Coastal trail.jpg', 'Portrait coastline.jpg')
}
Invoke-UI @('invoke', "Apply preset PNG $([char]0xB7) Lossless", '-a', "$AppPid") | Out-Null
Wait-Ready
Invoke-UI @('invoke', 'Select Coast at golden hour.jpg', '-a', "$AppPid") | Out-Null
Scroll-SettingsTop
Capture '06-image-conversion.png'
Convert-Demo
Invoke-UI @('invoke', 'HistoryTab', '-a', "$AppPid") | Out-Null
Invoke-UI @('wait-for', 'HistoryList', '-a', "$AppPid", '-t', '3000') | Out-Null
Capture '07-completed-history.png'
$outputs = @(Get-ChildItem -LiteralPath $DemoDirectory -File | Where-Object { $_.Extension -in '.mp4', '.mkv', '.png' })
if ($outputs.Count -ne 10) { throw "Expected ten real demo outputs, found $($outputs.Count)." }
$evidence = foreach ($output in $outputs) {
    $probe = & ffprobe -v error -show_entries 'format=duration:stream=codec_name,codec_type,width,height' -of json $output.FullName
    if ($LASTEXITCODE -ne 0) { throw "Could not probe actual output: $($output.Name)" }
    [ordered]@{ name = $output.Name; bytes = $output.Length; probe = $probe | ConvertFrom-Json }
}
$join = @($outputs | Where-Object Extension -eq '.mkv')
$joinSeconds = [double]((& ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 $join[0].FullName) -join '')
if ($LASTEXITCODE -ne 0 -or [math]::Abs($joinSeconds - 38) -gt 0.25) { throw "Unexpected joined duration: $joinSeconds" }
[ordered]@{
    capturedAt = [DateTimeOffset]::Now.ToString('O')
    build = 'Windows x64 Release development build; same source UI, not a certified Store installation'
    generatedUi = $false
    operations = @('3 video conversions, with first input trimmed to 1-10 seconds', '1 compatible join of 3 clips', '1 accurate split into 3 clips', '3 JPEG-to-PNG conversions')
    outputs = $evidence
} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $kit 'capture-evidence.json')
'Captured seven actual app states and verified ten real conversion outputs.'
