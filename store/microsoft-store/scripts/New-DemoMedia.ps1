param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$kit = Split-Path $PSScriptRoot
$image = Join-Path $kit 'source\coastline.png'
if (-not (Test-Path -LiteralPath $image -PathType Leaf)) { throw "Missing original demo image: $image" }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$clips = @(
    @{ Name = 'Coastal walk.mov'; Seconds = 12; X = 'iw/2-(iw/zoom/2)' },
    @{ Name = 'Headland sunset.mov'; Seconds = 16; X = 'iw-iw/zoom' },
    @{ Name = 'Ocean overlook.mov'; Seconds = 10; X = '0' }
)
foreach ($clip in $clips) {
    $filter = "zoompan=z='min(1.04+on*0.00025,1.14)':x='$($clip.X)':y='ih/2-(ih/zoom/2)':d=1:s=1280x720:fps=24,setsar=1"
    & ffmpeg -hide_banner -loglevel error -y -loop 1 -framerate 24 -i $image `
        -f lavfi -i 'anoisesrc=color=pink:amplitude=0.035:sample_rate=48000' `
        -f lavfi -i 'sine=frequency=220:sample_rate=48000' `
        -map 0:v -map 1:a -map 2:a -t $clip.Seconds -vf $filter `
        -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p `
        -x264-params 'keyint=48:min-keyint=48:scenecut=0:bframes=2' -video_track_timescale 24000 `
        -c:a aac -b:a 128k -ac 2 -ar 48000 -metadata:s:a:0 'title=Ambient bed' `
        -metadata:s:a:1 'title=Alternate mix' -movflags +faststart (Join-Path $OutputDirectory $clip.Name)
    if ($LASTEXITCODE -ne 0) { throw "Demo clip generation failed: $($clip.Name)" }
}
$photos = @(
    @{ Name = 'Coast at golden hour.jpg'; Crop = '1536x864+0+100' },
    @{ Name = 'Coastal trail.jpg'; Crop = '1200x900+0+90' },
    @{ Name = 'Portrait coastline.jpg'; Crop = '640x960+400+20' }
)
foreach ($photo in $photos) {
    & magick $image -crop $photo.Crop +repage -quality 94 (Join-Path $OutputDirectory $photo.Name)
    if ($LASTEXITCODE -ne 0) { throw "Demo photo generation failed: $($photo.Name)" }
}
Write-Output "Created three original demo clips and three photos in $OutputDirectory"
