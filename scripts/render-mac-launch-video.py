"""Render the held Mac website launch film from genuine app captures."""

import hashlib
import json
import math
import platform
import shutil
import subprocess
import tempfile
import wave
from functools import lru_cache
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageColor


ROOT = Path(__file__).resolve().parents[1]
STORE = ROOT / "store" / "mac-app-store" / "en-us"
SCREENSHOTS = STORE / "screenshots"
PREVIEWS = STORE / "app-previews"
WEBSITE = STORE / "website-launch"
LOGOS = ROOT / "store" / "microsoft-store" / "logos"
FONT_DIR = ROOT / "dotnet" / "src" / "LocalMorph.App" / "Resources" / "Fonts"

WIDTH, HEIGHT, FPS, SECONDS = 1920, 1080, 30, 30
BACKGROUND_TOP = "#0b1120"
BACKGROUND_BOTTOM = "#121c32"
PAPER = "#f3f6fc"
MUTED = "#a2b0c5"
BLUE = "#3978ff"
GREEN = "#b2e86b"
LINE = "#33425d"
SCENE_STARTS = (0, 5, 11, 17, 23)
CAPTURE_FILES = (
    "01-local-workspace.png",
    "02-video-presets.png",
    "03-conversion-complete.png",
    "04-conversion-history.png",
    "05-photo-conversion.png",
)
APP_PREVIEW = PREVIEWS / "localmorph-mac-app-preview.mp4"
APP_PREVIEW_POSTER = PREVIEWS / "localmorph-mac-app-preview-poster.png"
APP_PREVIEW_SOURCE = PREVIEWS / "source" / "localmorph-mac-app-preview-source.mov"
APP_PREVIEW_RENDERER = ROOT / "scripts" / "render-mac-app-preview.py"
WEBSITE_VIDEO = WEBSITE / "localmorph-mac-launch.mp4"
WEBSITE_POSTER = WEBSITE / "localmorph-mac-poster.webp"
STORYBOARD = WEBSITE / "storyboard.jpg"
CAPTIONS = WEBSITE / "captions.vtt"
TRANSCRIPT = WEBSITE / "transcript.md"
MANIFEST = STORE / "asset-manifest.json"


def ease(value):
    value = max(0, min(1, value))
    return 1 - (1 - value) ** 4


@lru_cache(maxsize=None)
def font(size, semibold=False):
    name = "OpenSans-Semibold.ttf" if semibold else "OpenSans-Regular.ttf"
    return ImageFont.truetype(str(FONT_DIR / name), size)


@lru_cache(maxsize=1)
def base_background():
    top = np.array(ImageColor.getrgb(BACKGROUND_TOP), dtype=np.float32)
    bottom = np.array(ImageColor.getrgb(BACKGROUND_BOTTOM), dtype=np.float32)
    vertical = np.linspace(0, 1, HEIGHT, dtype=np.float32)[:, None, None]
    rows = top * (1 - vertical) + bottom * vertical
    return Image.fromarray(np.broadcast_to(rows, (HEIGHT, WIDTH, 3)).astype(np.uint8))


@lru_cache(maxsize=1)
def logo():
    with Image.open(LOGOS / "app-tile-1024.png") as source:
        return source.convert("RGBA").resize((52, 52), Image.Resampling.LANCZOS)


@lru_cache(maxsize=1)
def app_icon():
    with Image.open(LOGOS / "app-tile-1024.png") as source:
        return source.convert("RGBA").resize((228, 228), Image.Resampling.LANCZOS)


@lru_cache(maxsize=5)
def screenshot(index):
    with Image.open(SCREENSHOTS / CAPTURE_FILES[index]) as source:
        if source.size != (2560, 1600):
            raise ValueError(f"{CAPTURE_FILES[index]} is {source.size}, expected 2560x1600.")
        if source.mode != "RGB" or not source.info.get("icc_profile"):
            raise ValueError(f"{CAPTURE_FILES[index]} must be flattened and color-managed.")
        return source.resize((1184, 740), Image.Resampling.LANCZOS)


def draw_text(image, message, xy, size, color, time, delay=0, semibold=False):
    progress = ease((time - delay) / 0.7)
    if not progress:
        return
    x, y = xy
    y += round((1 - progress) * 22)
    ImageDraw.Draw(image).text((x, y), message, font=font(size, semibold), fill=color)


def draw_header(image, final=False):
    image.paste(logo(), (94, 42), logo())
    draw = ImageDraw.Draw(image)
    draw.text((160, 48), "LocalMorph", font=font(38, True), fill=PAPER)
    draw.rounded_rectangle((1612, 48, 1824, 96), radius=22, fill="#1d2b45")
    draw.text((1651, 59), "FOR MAC", font=font(18, True), fill=GREEN if final else MUTED)


def draw_footer(image, time):
    draw = ImageDraw.Draw(image)
    draw.line((96, 1018, 1824, 1018), fill=LINE, width=2)
    position = 96 + round(1728 * time / SECONDS)
    draw.line((96, 1018, position, 1018), fill=BLUE, width=4)
    draw.ellipse((position - 6, 1012, position + 6, 1024), fill=GREEN)
    draw.text((96, 1037), "LOCAL BY DESIGN", font=font(16, True), fill=MUTED)
    draw.text((1636, 1033), "localmorph.com", font=font(21, True), fill=PAPER)


def background(time):
    image = base_background().copy()
    draw = ImageDraw.Draw(image, "RGBA")
    for index in range(8):
        x = 120 + index * 240
        draw.line((x, 135, x, 994), fill=(135, 165, 220, 12), width=1)
    center_x = 1470 + round(52 * math.sin(time * 0.23))
    center_y = 520 + round(38 * math.cos(time * 0.19))
    draw.ellipse((center_x - 390, center_y - 390, center_x + 390, center_y + 390),
                 fill=(38, 92, 210, 13))
    for index in range(7):
        x = 1270 + index * 82
        y = 200 + ((index * 137 + round(time * 14)) % 690)
        draw.ellipse((x, y, x + 3, y + 3), fill=(170, 200, 255, 80))
    return image


def reveal_lines(image, lines, x, y, size, color, time, delay=0, line_gap=1.16):
    for index, line in enumerate(lines):
        draw_text(image, line, (x, y + round(index * size * line_gap)), size, color,
                  time, delay + index * 0.14, True)


def hero_graphic(image, time):
    draw = ImageDraw.Draw(image, "RGBA")
    labels = (("VIDEO", 268), ("PHOTO", 470), ("AUDIO", 672))
    for index, (label, y) in enumerate(labels):
        x = 1004 + round((1 - ease((time - index * 0.18) / 0.9)) * 48)
        draw.rounded_rectangle((x, y, x + 210, y + 88), radius=18,
                               fill=(28, 42, 66, 230), outline=(99, 127, 176, 180), width=2)
        draw.ellipse((x + 22, y + 27, x + 54, y + 59),
                     outline=BLUE if index != 1 else GREEN, width=3)
        draw.text((x + 72, y + 25), label, font=font(22, True), fill=PAPER)
        end_x = 1370 + round(50 * ease((time - 0.4) / 1.8))
        draw.line((x + 210, y + 44, end_x, y + 44), fill=(77, 116, 190, 200), width=3)
        dot_x = x + 210 + (end_x - x - 210) * ((time * 0.65 + index * 0.28) % 1)
        draw.ellipse((round(dot_x - 6), y + 38, round(dot_x + 6), y + 50), fill=GREEN)
    icon = app_icon()
    image.paste(icon, (1415, 398), icon)
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((1398, 381, 1660, 643), radius=38, outline="#435980", width=2)


def app_capture(image, index, local_time):
    width, height = 1160, 725
    shift = round(24 * ease(local_time / 6))
    x, y = 690, 222
    tile = screenshot(index).crop((shift, round(15 * ease(local_time / 6)),
                                   shift + width, round(15 * ease(local_time / 6)) + height))
    shadow = ImageDraw.Draw(image)
    shadow.rounded_rectangle((x + 12, y + 16, x + width + 12, y + height + 16),
                             radius=20, fill="#070b13")
    mask = Image.new("L", (width, height), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, width - 1, height - 1), radius=16, fill=255)
    image.paste(tile, (x, y), mask)
    ImageDraw.Draw(image).rounded_rectangle((x, y, x + width, y + height),
                                             radius=16, outline="#3a4b69", width=2)


def feature_scene(index, time):
    image = background(time)
    draw_header(image, final=index == 4)
    titles = (
        (("One clear", "workspace."), ("Bring videos, audio and photos together.",
                                        "Preview the files in your batch."), 0),
        (("Set a size.", "See it through."), ("Choose a target for sharing.",
                                              "Review the completed result."), 2),
        (("Your history.", "At a glance."), ("Review completed conversions.",
                                             "Open or repeat a result from history."), 3),
        (("A new format.", "Same device."), ("Preview the original, then convert.",
                                              "Files stay on this device."), 4),
    )
    title, description, capture_index = titles[index - 1]
    reveal_lines(image, title, 96, 282, 67, PAPER, time, 0.1)
    for row, line in enumerate(description):
        draw_text(image, line, (101, 504 + row * 48), 25, MUTED, time, 0.48 + row * 0.18)
    ImageDraw.Draw(image).rounded_rectangle((101, 638, 233, 644), radius=3, fill=BLUE)
    if index == 4:
        draw = ImageDraw.Draw(image)
        draw.rounded_rectangle((98, 716, 465, 779), radius=16, fill="#1b2b46",
                               outline="#4a6794", width=2)
        draw.text((123, 734), "LEARN MORE  ·  localmorph.com", font=font(20, True), fill=PAPER)
    app_capture(image, capture_index, time)
    return image


def intro_scene(time):
    image = background(time)
    draw_header(image)
    reveal_lines(image, ("Your files.", "Your Mac."), 96, 292, 104, PAPER, time, 0.15, 1.14)
    draw_text(image, "A calmer way to convert.", (103, 557), 34, MUTED, time, 0.55)
    draw_text(image, "Video  ·  audio  ·  photos", (103, 629), 23, GREEN, time, 0.85, True)
    hero_graphic(image, time)
    return image


def scene(index, time):
    return intro_scene(time) if index == 0 else feature_scene(index, time)


def frame(time):
    index = max(i for i, start in enumerate(SCENE_STARTS) if time >= start)
    local_time = time - SCENE_STARTS[index]
    image = scene(index, local_time)
    if index and local_time < 0.42:
        previous_end = (SCENE_STARTS[index] - SCENE_STARTS[index - 1])
        previous = scene(index - 1, previous_end)
        image = Image.blend(previous, image, ease(local_time / 0.42))
    draw_footer(image, time)
    return image


def soundtrack(path):
    rate = 48000
    sample_count = rate * SECONDS
    mix = np.zeros((sample_count, 2), dtype=np.float64)
    beat = 60 / 80
    progression = (
        (50, (62, 65, 69, 74)),
        (46, (58, 62, 65, 69)),
        (43, (55, 58, 62, 67)),
        (48, (60, 64, 67, 72)),
    )

    def add(signal, start, gain, pan=0):
        offset = round(start * rate)
        count = min(len(signal), sample_count - offset)
        if count <= 0:
            return
        mix[offset:offset + count, 0] += signal[:count] * gain * math.sqrt((1 - pan) / 2)
        mix[offset:offset + count, 1] += signal[:count] * gain * math.sqrt((1 + pan) / 2)

    def note(number, duration):
        t = np.arange(round(duration * rate)) / rate
        hz = 440 * 2 ** ((number - 69) / 12)
        signal = (np.sin(2 * np.pi * hz * t)
                  + 0.2 * np.sin(4 * np.pi * hz * t) * np.exp(-t * 3))
        envelope = (1 - np.exp(-t * 10)) * np.exp(-t * 2)
        envelope *= np.minimum(1, (duration - t) / 0.18)
        return signal * envelope

    for bar in range(10):
        start = bar * beat * 4
        root, chord = progression[bar % len(progression)]
        t = np.arange(round(3.6 * rate)) / rate
        pad = sum(np.sin(2 * np.pi * 440 * 2 ** ((number - 69) / 12) * t) for number in chord)
        pad *= np.minimum(1, t / 0.55) * np.minimum(1, (3.6 - t) / 0.8)
        add(pad, start, 0.035)
        add(note(root, 1.6), start, 0.11)
        add(note(root + 12, 1.4), start + beat * 2, 0.06)
        for step, chord_index in enumerate((0, 2, 1, 3)):
            add(note(chord[chord_index] + 12, 1.1),
                start + beat * (step + 0.5), 0.052, -0.3 if step % 2 else 0.3)
    seconds = np.arange(sample_count) / rate
    mix *= np.minimum(1, seconds / 0.9)[:, None]
    mix *= np.minimum(1, (SECONDS - seconds) / 1.5)[:, None]
    mix *= 0.8 / max(1, np.max(np.abs(mix)))
    with wave.open(str(path), "wb") as output:
        output.setnchannels(2)
        output.setsampwidth(2)
        output.setframerate(rate)
        output.writeframes((mix * 32767).astype("<i2").tobytes())


def verify_video(path):
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)],
        check=True, capture_output=True, text=True,
    )
    data = json.loads(probe.stdout)
    video = next(stream for stream in data["streams"] if stream["codec_type"] == "video")
    audio = next(stream for stream in data["streams"] if stream["codec_type"] == "audio")
    assert (video["width"], video["height"]) == (WIDTH, HEIGHT)
    assert video["codec_name"] == "h264" and video["pix_fmt"] == "yuv420p"
    assert video["r_frame_rate"] == "30/1" and int(video["nb_frames"]) == FPS * SECONDS
    assert abs(float(data["format"]["duration"]) - SECONDS) < 0.1
    assert audio["codec_name"] == "aac" and audio["channels"] == 2
    assert int(audio["sample_rate"]) == 48000
    return data


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def file_record(path):
    return {
        "path": path.relative_to(ROOT).as_posix(),
        "sha256": sha256(path),
        "bytes": path.stat().st_size,
    }


def media_record(path):
    metadata = verify_video(path)
    video = next(stream for stream in metadata["streams"] if stream["codec_type"] == "video")
    audio = next(stream for stream in metadata["streams"] if stream["codec_type"] == "audio")
    return {
        "path": path.relative_to(ROOT).as_posix(),
        "sha256": sha256(path),
        "bytes": path.stat().st_size,
        "width": video["width"],
        "height": video["height"],
        "durationSeconds": round(float(metadata["format"]["duration"]), 3),
        "frameRate": video["r_frame_rate"],
        "videoCodec": video["codec_name"],
        "pixelFormat": video["pix_fmt"],
        "audioCodec": audio["codec_name"],
        "audioChannels": audio["channels"],
        "audioSampleRate": int(audio["sample_rate"]),
    }


def source_record(path):
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)],
        check=True, capture_output=True, text=True,
    )
    metadata = json.loads(probe.stdout)
    video = next(stream for stream in metadata["streams"] if stream["codec_type"] == "video")
    return {
        "path": path.relative_to(ROOT).as_posix(),
        "sha256": sha256(path),
        "bytes": path.stat().st_size,
        "width": video["width"],
        "height": video["height"],
        "durationSeconds": round(float(metadata["format"]["duration"]), 3),
        "frameRate": video["avg_frame_rate"],
    }


def build_manifest():
    existing_manifest = json.loads(MANIFEST.read_text(encoding="utf-8")) if MANIFEST.exists() else {}
    existing_provenance = existing_manifest.get("provenance", {})
    screenshots = []
    for name in CAPTURE_FILES:
        path = SCREENSHOTS / name
        with Image.open(path) as image:
            if image.size != (2560, 1600):
                raise ValueError(f"{name} is not a 2560x1600 capture.")
            if image.mode != "RGB" or not image.info.get("icc_profile"):
                raise ValueError(f"{name} must be flattened to opaque RGB with an ICC profile.")
            screenshots.append({
                "path": path.relative_to(ROOT).as_posix(),
                "width": image.width,
                "height": image.height,
                "mode": image.mode,
                "hasIccProfile": bool(image.info.get("icc_profile")),
                "sha256": sha256(path),
            })
    app_preview = media_record(APP_PREVIEW)
    website_film = media_record(WEBSITE_VIDEO)
    ffmpeg_version = subprocess.run(
        ["ffmpeg", "-version"], check=True, capture_output=True, text=True,
    ).stdout.splitlines()[0]
    if app_preview["width"] != 1920 or app_preview["height"] != 1080:
        raise ValueError("The App Store preview must be 1920x1080.")
    for path in (APP_PREVIEW_POSTER, WEBSITE_POSTER, CAPTIONS, TRANSCRIPT, STORYBOARD):
        if not path.is_file():
            raise FileNotFoundError(path)
    manifest = {
        "status": "owner-review; not submitted to App Store Connect or published on the website",
        "capture": {
            "appBundleId": "com.refractored.localmorph.assetcapture",
            "appVersion": "1.0.0",
            "buildNumber": "1",
            "sourceCommit": "40267fa557da8c08e28fb70949409f9895dff6e7",
            "host": "macOS 26.6.2, Xcode 26.6, .NET SDK 10.0.401",
            "notes": [
                "Genuine app captures from an isolated asset-capture build, not a TestFlight or App Store build.",
                "The capture-only app used the network.server entitlement for local DevFlow automation; shipping Store entitlements were not changed.",
                "Recapture and review the set against the final TestFlight candidate before App Store submission.",
            ],
        },
        "renderer": {
            **file_record(Path(__file__).resolve()),
            "pythonVersion": platform.python_version(),
            "ffmpegVersion": ffmpeg_version,
        },
        "appPreviewRenderer": file_record(APP_PREVIEW_RENDERER),
        "screenshots": screenshots,
        "marketingScreenshotRenderer": existing_manifest.get("marketingScreenshotRenderer"),
        "marketingScreenshotTreatment": existing_manifest.get("marketingScreenshotTreatment"),
        "marketingScreenshots": existing_manifest.get("marketingScreenshots", []),
        "appStorePreview": {
            **app_preview,
            "poster": file_record(APP_PREVIEW_POSTER),
            "sourceRecording": source_record(APP_PREVIEW_SOURCE),
            "source": "Genuine 2560x1440 app-window recording, encoded at 1920x1080/30fps.",
            "audioNote": "Silent stereo AAC compatibility track; no music or narration.",
        },
        "websiteLaunchFilm": {
            **website_film,
            "poster": file_record(WEBSITE_POSTER),
            "captions": file_record(CAPTIONS),
            "transcript": file_record(TRANSCRIPT),
            "storyboard": file_record(STORYBOARD),
            "publication": "Held in the App Store kit; not wired into the public website and makes no availability claim.",
        },
        "provenance": {
            "appScreenshots": "Root screenshots are genuine native captures; marketing variants add copy and artwork only around the unaltered app capture.",
            "brandMark": "Existing LocalMorph app-tile source asset in store/microsoft-store/logos/.",
            "typeface": "Open Sans files already bundled in the application font resources.",
            "music": "Original instrumental synthesized by this renderer; no third-party recording.",
            "demoMedia": existing_provenance.get(
                "demoMedia",
                "Coastline demonstration photo/video origin is not recorded; owner rights clearance is required before public or App Store use.",
            ),
        },
    }
    MANIFEST.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


def make_storyboard():
    times = (2.5, 8, 14, 20, 26, 29)
    sheet = Image.new("RGB", (960, 810), BACKGROUND_TOP)
    for index, time in enumerate(times):
        tile = frame(time).resize((480, 270), Image.Resampling.LANCZOS)
        sheet.paste(tile, ((index % 2) * 480, (index // 2) * 270))
    sheet.save(STORYBOARD, quality=92)


def main():
    for command in ("ffmpeg", "ffprobe"):
        if not shutil.which(command):
            raise RuntimeError(f"{command} is required on PATH.")
    WEBSITE.mkdir(parents=True, exist_ok=True)
    for path in (APP_PREVIEW, APP_PREVIEW_POSTER, APP_PREVIEW_SOURCE, CAPTIONS, TRANSCRIPT):
        if not path.is_file():
            raise FileNotFoundError(path)
    for index in range(len(CAPTURE_FILES)):
        screenshot(index)
    make_storyboard()
    frame(8).save(WEBSITE_POSTER, "WEBP", quality=90, method=6)
    with tempfile.TemporaryDirectory(prefix="localmorph-mac-launch-") as temporary:
        music = Path(temporary) / "original-instrumental.wav"
        log = Path(temporary) / "ffmpeg.log"
        soundtrack(music)
        command = [
            "ffmpeg", "-y", "-hide_banner", "-loglevel", "warning",
            "-f", "rawvideo", "-pixel_format", "rgb24", "-video_size", f"{WIDTH}x{HEIGHT}",
            "-framerate", str(FPS), "-i", "pipe:0", "-i", str(music),
            "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
            "-vf", "scale=in_range=full:out_range=tv:out_color_matrix=bt709",
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000",
            "-af", "loudnorm=I=-16:TP=-1.5:LRA=8",
            "-t", str(SECONDS), "-movflags", "+faststart",
            "-metadata", "title=LocalMorph for Mac - Website Launch Film",
            "-metadata", "comment=Genuine Mac captures; original instrumental; no availability claim.",
            str(WEBSITE_VIDEO),
        ]
        with log.open("wb") as errors:
            process = subprocess.Popen(command, stdin=subprocess.PIPE, stderr=errors)
            try:
                for number in range(FPS * SECONDS):
                    process.stdin.write(frame(number / FPS).tobytes())
                    if number % (FPS * 5) == 0:
                        print(f"Rendered {number // FPS}/{SECONDS} seconds", flush=True)
                process.stdin.close()
                result = process.wait()
            except BaseException:
                if process.poll() is None:
                    process.kill()
                process.wait()
                print(log.read_text(encoding="utf-8"), flush=True)
                raise
        if result:
            raise RuntimeError(log.read_text(encoding="utf-8"))
    verify_video(WEBSITE_VIDEO)
    build_manifest()
    print(f"Verified website film: {WEBSITE_VIDEO} ({WEBSITE_VIDEO.stat().st_size / 1_000_000:.1f} MB)")
    print(f"Verified asset manifest: {MANIFEST}")


if __name__ == "__main__":
    main()
