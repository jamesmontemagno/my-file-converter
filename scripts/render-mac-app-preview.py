"""Encode and validate the genuine Mac App Store screen-recording preview."""

import json
import shutil
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PREVIEWS = ROOT / "store" / "mac-app-store" / "en-us" / "app-previews"
SOURCE = PREVIEWS / "source" / "localmorph-mac-app-preview-source.mov"
OUTPUT = PREVIEWS / "localmorph-mac-app-preview.mp4"
POSTER = PREVIEWS / "localmorph-mac-app-preview-poster.png"
WIDTH, HEIGHT, FPS, SECONDS = 1920, 1080, 30, 30
MAX_BYTES = 500_000_000


def probe(path):
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)],
        check=True, capture_output=True, text=True,
    )
    return json.loads(result.stdout)


def validate(path):
    metadata = probe(path)
    video = next(stream for stream in metadata["streams"] if stream["codec_type"] == "video")
    audio = next(stream for stream in metadata["streams"] if stream["codec_type"] == "audio")
    duration = float(metadata["format"]["duration"])
    assert (video["width"], video["height"]) == (WIDTH, HEIGHT)
    assert video["codec_name"] == "h264" and video["pix_fmt"] == "yuv420p"
    assert video["r_frame_rate"] == f"{FPS}/1" and int(video["nb_frames"]) == FPS * SECONDS
    assert abs(duration - SECONDS) < 0.1
    assert audio["codec_name"] == "aac" and audio["channels"] == 2
    assert int(audio["sample_rate"]) == 48000
    assert path.stat().st_size <= MAX_BYTES
    return metadata


def main():
    for command in ("ffmpeg", "ffprobe"):
        if not shutil.which(command):
            raise RuntimeError(f"{command} is required on PATH.")
    if not SOURCE.is_file():
        raise FileNotFoundError(SOURCE)
    source_video = next(
        stream for stream in probe(SOURCE)["streams"] if stream["codec_type"] == "video"
    )
    if (source_video["width"], source_video["height"]) != (2560, 1440):
        raise ValueError("The source must be a 2560x1440 genuine Mac app-window recording.")
    PREVIEWS.mkdir(parents=True, exist_ok=True)
    subprocess.run([
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
        "-i", str(SOURCE),
        "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
        "-map", "0:v:0", "-map", "1:a:0",
        "-vf", f"fps={FPS},scale={WIDTH}:{HEIGHT}:flags=lanczos,setsar=1,format=yuv420p",
        "-t", str(SECONDS), "-r", str(FPS), "-fps_mode", "cfr",
        "-c:v", "libx264", "-preset", "slow", "-crf", "18",
        "-profile:v", "high", "-level:v", "4.1", "-pix_fmt", "yuv420p",
        "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709",
        "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2",
        "-shortest", "-movflags", "+faststart",
        "-metadata", "title=LocalMorph for Mac - App Store Preview",
        "-metadata", "comment=Genuine Mac app screen recording; silent stereo AAC compatibility track.",
        str(OUTPUT),
    ], check=True)
    metadata = validate(OUTPUT)
    subprocess.run([
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
        "-ss", "18", "-i", str(OUTPUT), "-frames:v", "1", str(POSTER),
    ], check=True)
    video = next(stream for stream in metadata["streams"] if stream["codec_type"] == "video")
    print(json.dumps({
        "preview": str(OUTPUT.relative_to(ROOT)),
        "bytes": OUTPUT.stat().st_size,
        "poster": str(POSTER.relative_to(ROOT)),
        "video": {
            key: video[key]
            for key in ("codec_name", "width", "height", "pix_fmt", "r_frame_rate", "nb_frames")
        },
        "durationSeconds": float(metadata["format"]["duration"]),
    }, indent=2))


if __name__ == "__main__":
    main()
