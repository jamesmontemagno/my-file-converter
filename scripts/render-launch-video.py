"""Render the Windows launch film with real screenshots and an original soundtrack."""

import json
import math
import os
import shutil
import subprocess
import tempfile
import wave
from functools import lru_cache
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
MEDIA = ROOT / "public" / "media"
STORE = ROOT / "store" / "microsoft-store"
WIDTH, HEIGHT, FPS, SECONDS = 1920, 1080, 30, 30
PAPER, INK, BLUE, LIME = "#f3f7ff", "#091b35", "#1458ff", "#caff31"
MUTED, LINE = "#3b5272", "#bdd0ef"
LAUNCH_URL = "localmorph.com"
FONT_DIR = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts"
BODY_FONTS = ROOT / "dotnet" / "src" / "LocalMorph.App" / "Resources" / "Fonts"
SCENES = (0, 5, 11, 17, 23)
CAPTURES = (
    "01-batch-conversion.png",
    "03-preview-trim-tracks.png",
)


@lru_cache(maxsize=None)
def font(size, body=False):
    path = BODY_FONTS / "OpenSans-Regular.ttf" if body else FONT_DIR / "bahnschrift.ttf"
    return ImageFont.truetype(str(path), size)


def ease(value):
    value = max(0, min(1, value))
    return 1 - (1 - value) ** 4


def text(image, message, xy, size, fill=INK, delay=0, time=10, body=False):
    """Reveal type through a fixed mask instead of bouncing entire title cards."""
    progress = ease((time - delay) / 0.85)
    if not progress:
        return
    if progress == 1:
        ImageDraw.Draw(image).text(xy, message, font=font(size, body), fill=fill)
        return
    layer = Image.new("RGBA", (WIDTH, HEIGHT))
    draw = ImageDraw.Draw(layer)
    x, y = xy
    draw.text((x, y + (1 - progress) * 26), message, font=font(size, body), fill=fill)
    mask = Image.new("L", (WIDTH, HEIGHT))
    ImageDraw.Draw(mask).rectangle((0, 0, WIDTH, y + size * 1.45 * progress), fill=255)
    image.paste(layer, (0, 0), Image.composite(layer.getchannel("A"), Image.new("L", layer.size), mask))


def paragraph(image, lines, xy, size=29, fill=MUTED):
    x, y = xy
    for line in lines:
        text(image, line, (x, y), size, fill, body=True)
        y += size * 1.6


@lru_cache(maxsize=None)
def logo(size):
    with Image.open(STORE / "logos" / "app-tile-1024.png") as source:
        return source.convert("RGBA").resize((size, size), Image.Resampling.LANCZOS)


@lru_cache(maxsize=None)
def screenshot(index):
    with Image.open(STORE / "en-us" / "screenshots" / CAPTURES[index]) as source:
        return source.convert("RGB").resize((1160, 653), Image.Resampling.LANCZOS)


def header(image, final=False):
    image.paste(logo(56), (96, 65), logo(56))
    text(image, "LocalMorph", (170, 70), 40, PAPER if final else INK)
    text(image, "FOR WINDOWS", (1535, 83), 23, PAPER if final else MUTED)


def rail(image, time, final=False):
    draw = ImageDraw.Draw(image)
    draw.line((96, 1014, 1824, 1014), fill="#5688ff" if final else LINE, width=2)
    x = 96 + 1728 * time / SECONDS
    draw.line((96, 1014, x, 1014), fill=LIME if final else BLUE, width=4)
    draw.ellipse((x - 6, 1008, x + 6, 1020), fill=LIME if final else BLUE)
    text(image, "LOCAL BY DESIGN", (96, 1031), 17, PAPER if final else MUTED)
    text(image, "localmorph.com", (1635, 1028), 22, PAPER if final else MUTED)


def arrow(draw, start, end, fill=BLUE, width=5):
    x1, y = start
    x2, _ = end
    draw.line((x1, y, x2, y), fill=fill, width=width)
    draw.line((x2 - 17, y - 14, x2, y, x2 - 17, y + 14), fill=fill, width=width)


def route_signal(draw, points, progress, fill=LIME, radius=8):
    lengths = [math.dist(a, b) for a, b in zip(points, points[1:])]
    remaining = (progress % 1) * sum(lengths)
    for a, b, length in zip(points, points[1:], lengths):
        if remaining <= length:
            fraction = remaining / length
            x = a[0] + (b[0] - a[0]) * fraction
            y = a[1] + (b[1] - a[1]) * fraction
            draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=fill)
            return
        remaining -= length


def file_label(image, label, xy, active=False):
    x, y = xy
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((x, y, x + 190, y + 94), radius=12, fill=BLUE if active else "white")
    draw.line((x + 25, y + 25, x + 25, y + 67, x + 54, y + 67, x + 54, y + 37, x + 42, y + 25, x + 25, y + 25),
              fill="white" if active else BLUE, width=3)
    text(image, label, (x + 74, y + 27), 33, "white" if active else INK)


def app_frame(image, index, local_time):
    # Keep the actual window intact; the camera settles gently, never fabricating UI.
    progress = ease(local_time / 1.3)
    scale = 0.96 + 0.04 * progress + 0.012 * min(local_time / 6, 1)
    source = screenshot(index)
    size = (round(source.width * scale), round(source.height * scale))
    tile = source.resize(size, Image.Resampling.BICUBIC)
    x = 694 + round((source.width - size[0]) / 2)
    y = 244 + round((1 - progress) * 55)
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((x + 10, y + 14, x + size[0] + 10, y + size[1] + 14), radius=7, fill=LINE)
    image.paste(tile, (x, y))
    text(image, "ACTUAL WINDOWS APP", (x, 931), 20, MUTED)


def intro(time):
    image = Image.new("RGB", (WIDTH, HEIGHT), PAPER)
    header(image)
    text(image, "Your files.", (96, 265), 114, delay=0.1, time=time)
    text(image, "Your computer.", (96, 395), 114, delay=0.4, time=time)
    text(image, "Now on Windows.", (96, 535), 96, BLUE, delay=0.8, time=time)
    paragraph(image, ["The format you need.", "Right where you are."], (102, 730), 32)
    draw = ImageDraw.Draw(image)
    x = 1226
    file_label(image, "MOV", (x - 100, 292 + (1 - ease(time / 0.8)) * 60))
    file_label(image, "MP4", (x + 260 + (1 - ease((time - 1) / 0.9)) * 100, 666), active=True)
    progress = ease((time - 0.5) / 1.7)
    path = [(x - 5, 386), (x - 5, 500), (x + 170, 500), (x + 170, 713), (x + 260, 713)]
    remaining = progress * sum(math.dist(a, b) for a, b in zip(path, path[1:]))
    for a, b in zip(path, path[1:]):
        length = math.dist(a, b)
        part = min(1, max(0, remaining / length))
        end = (a[0] + (b[0] - a[0]) * part, a[1] + (b[1] - a[1]) * part)
        if part:
            draw.line((*a, *end), fill=BLUE, width=5)
        remaining -= length
    mark = logo(194)
    image.paste(mark, (x + 73, 403), mark)
    if time > 1.7:
        route_signal(draw, path, (time - 1.7) / 2.1, BLUE, 10)
    text(image, "VIDEO  /  AUDIO  /  IMAGES", (1120, 845), 25, MUTED)
    return image


def local_conversion_graphic(image, time):
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((694, 225, 1830, 925), radius=14, outline=LINE, width=2)
    text(image, "ON YOUR PC", (744, 255), 23, MUTED)
    for row, (kind, source, output) in enumerate((
        ("VIDEO", "MOV", "MP4"),
        ("AUDIO", "FLAC", "MP3"),
        ("IMAGES", "PNG", "WEBP"),
    )):
        y = 349 + row * 173
        text(image, kind, (744, y - 34), 20, MUTED)
        file_label(image, source, (744, y))
        arrow(draw, (961, y + 47), (1540, y + 47), fill=LINE, width=3)
        progress = max(0, time - row * 0.27)
        route_signal(draw, [(961, y + 47), (1540, y + 47)], progress / 2.15, BLUE, 9)
        mark = logo(68)
        image.paste(mark, (1215, y + 13), mark)
        settle = ease((time - 0.55 - row * 0.2) / 0.9)
        file_label(image, output, (1570 + (1 - settle) * 35, y), active=True)
    text(image, "Files in. New formats out. Nothing uploaded.", (744, 865), 27, MUTED)


def batch_graphic(image, time):
    draw = ImageDraw.Draw(image)
    text(image, "ONE LOCAL QUEUE", (100, 697), 22, MUTED)
    for row in range(3):
        enter = ease((time - row * 0.2) / 0.8)
        x, y = 100 + (1 - enter) * 35, 750 + row * 70
        text(image, f"clip-0{row + 1}.mov", (x, y), 28)
        progress = ease((time - 1 - row * 0.55) / 2.6)
        draw.line((x, y + 44, x + 495, y + 44), fill=LINE, width=5)
        if progress:
            draw.line((x, y + 44, x + 495 * progress, y + 44), fill=BLUE, width=5)
        if progress == 1:
            draw.ellipse((x + 461, y + 3, x + 491, y + 33), fill=LIME)
            draw.line((x + 468, y + 17, x + 474, y + 23, x + 484, y + 12), fill=INK, width=3)


def edit_graphic(image, time):
    draw = ImageDraw.Draw(image)
    left, right = 100, 600
    trim = ease((time - 0.4) / 1.9)
    start, end = left + 65 * trim, right - 85 * trim
    splitting = ease((time - 3) / 1.3)
    text(image, "TRIM YOUR CLIP" if time < 3 else "SPLIT INTO SHORTER CLIPS", (100, 702), 21, MUTED)
    draw.rounded_rectangle((left, 748, right, 796), radius=5, fill=LINE)
    segment = (end - start) / 3
    for index in range(3):
        x = start + index * segment + (index - 1) * splitting * 17
        draw.rounded_rectangle((x + 2, 744, x + segment - 2, 800), radius=4, fill=BLUE)
    for x in (start - splitting * 17, end + splitting * 17):
        draw.rounded_rectangle((x - 5, 735, x + 5, 809), radius=3, fill=INK)
    cursor = start + (end - start) * min(1, max(0, (time - 1.2) / 3))
    draw.line((cursor, 752, cursor, 791), fill=LIME, width=4)
    text(image, "EXTRACT AUDIO", (100, 836), 21, MUTED)
    for index in range(100):
        x = 102 + index * 5
        amplitude = 5 + 20 * abs(math.sin(index * 0.31) * math.cos(index * 0.17))
        fill = BLUE if index / 100 < max(0, (time - 0.8) / 4) else LINE
        draw.line((x, 899 - amplitude, x, 899 + amplitude), fill=fill, width=3)
    text(image, "Join compatible clips, too.", (100, 944), 25, MUTED)


def feature(index, time):
    image = Image.new("RGB", (WIDTH, HEIGHT), PAPER)
    header(image)
    titles = (
        ("New format.", "Same privacy."),
        ("Bring the", "whole batch."),
        ("Keep the part", "that matters."),
    )
    for row, title in enumerate(titles[index]):
        text(image, title, (96, 240 + row * 106), 84, BLUE if row else INK,
             delay=0.14 * row, time=time)
    descriptions = (
        ["Video, audio, and images.", "Converted on your PC.", "No conversion-server uploads."],
        ["Add files or folders.", "Choose a quick preset.", "Follow every file in the queue."],
        ["Preview and trim clips.", "Extract the audio you need.", "Make the next edit yours."],
    )
    paragraph(image, descriptions[index], (101, 510), 28)
    if index == 0:
        local_conversion_graphic(image, time)
        text(image, "YOUR FILES STAY ON YOUR DEVICE", (101, 774), 24, BLUE)
    elif index == 1:
        app_frame(image, 0, time)
        batch_graphic(image, time)
    else:
        app_frame(image, 1, time)
        edit_graphic(image, time)
    return image


def outro(time):
    image = Image.new("RGB", (WIDTH, HEIGHT), BLUE)
    header(image, final=True)
    text(image, "LocalMorph", (96, 245), 124, "white", time=time)
    text(image, "for Windows.", (96, 383), 124, "white", delay=0.15, time=time)
    text(image, "Your files. Your computer.", (103, 563), 46, "white", delay=0.3, time=time)
    draw = ImageDraw.Draw(image)
    text(image, "Get the Windows app at", (103, 660), 39, "white")
    text(image, LAUNCH_URL, (96, 717), 100, "white", delay=0.4, time=time)
    draw.line((103, 833, 103 + 760 * ease((time - 0.7) / 1.2), 833), fill=LIME, width=5)
    text(image, "Available on the Microsoft Store", (103, 859), 30, "white")
    mark = logo(348)
    image.paste(mark, (1334, 334), mark)
    arrow(draw, (1110, 508), (1287, 508), fill=PAPER, width=7)
    arrow(draw, (1724, 508), (1820, 508), fill=LIME, width=7)
    route_signal(draw, [(1110, 508), (1287, 508)], time / 2, PAPER, 12)
    route_signal(draw, [(1724, 508), (1820, 508)], time / 2 + 0.5, LIME, 12)
    paragraph(image, ["FFmpeg and FFprobe installed separately.", "The app's Tools page guides setup."],
              (103, 922), 22, "white")
    return image


def scene(index, time):
    if index == 0:
        return intro(time)
    if index == 4:
        return outro(time)
    return feature(index - 1, time)


def frame(time):
    index = max(i for i, start in enumerate(SCENES) if time >= start)
    local_time = time - SCENES[index]
    image = scene(index, local_time)
    if index and local_time < 0.45:
        previous = scene(index - 1, SCENES[index] - SCENES[index - 1] + local_time)
        image = Image.blend(previous, image, ease(local_time / 0.45))
    rail(image, time, final=index == 4)
    return image


def soundtrack(path):
    """Compose a deterministic, original 80 BPM electric-piano instrumental."""
    rate = 48000
    samples = int(SECONDS * rate)
    mix = np.zeros((samples, 2), dtype=np.float64)
    rng = np.random.default_rng(1776)
    beat = 60 / 80

    def add(signal, start, gain, pan=0):
        offset = int(start * rate)
        count = min(len(signal), samples - offset)
        if count <= 0:
            return
        mix[offset:offset + count, 0] += signal[:count] * gain * math.sqrt((1 - pan) / 2)
        mix[offset:offset + count, 1] += signal[:count] * gain * math.sqrt((1 + pan) / 2)

    def tone(note, duration, pad=False):
        t = np.arange(int(duration * rate)) / rate
        hz = 440 * 2 ** ((note - 69) / 12)
        if pad:
            signal = (np.sin(2 * np.pi * hz * t)
                      + 0.22 * np.sin(2 * np.pi * hz * 1.002 * t)
                      + 0.12 * np.sin(4 * np.pi * hz * t))
            envelope = np.minimum(1, t / 0.45) * np.minimum(1, (duration - t) / 0.8)
        else:
            signal = (np.sin(2 * np.pi * hz * t)
                      + 0.28 * np.sin(4 * np.pi * hz * t) * np.exp(-t * 3)
                      + 0.1 * np.sin(6 * np.pi * hz * t) * np.exp(-t * 5))
            envelope = (1 - np.exp(-t * 120)) * np.exp(-t * 2.2)
            envelope *= np.minimum(1, (duration - t) / 0.12)
        return signal * envelope

    chords = (
        (38, (62, 66, 69, 73, 76)),
        (35, (59, 62, 66, 69, 73)),
        (31, (55, 59, 62, 66, 69)),
        (33, (57, 61, 64, 69, 71)),
    )
    for bar in range(10):
        start = bar * beat * 4
        bass, chord = chords[bar % len(chords)]
        for i, note in enumerate(chord):
            add(tone(note, 3.65, pad=True), start, 0.035, (i - 2) * 0.28)
        for i, step in enumerate((0, 1.5, 2.5, 3.5)):
            note = chord[(i + bar) % len(chord)]
            pluck = tone(note + 12, 1.7)
            onset = start + step * beat
            add(pluck, onset, 0.075, -0.35 if i % 2 else 0.35)
            add(pluck, onset + beat * 0.75, 0.018, 0.5 if i % 2 else -0.5)
        for step in (0, 2):
            add(tone(bass, 1.4), start + step * beat, 0.14)
        for step in range(4):
            onset = start + step * beat
            if step % 2 == 0:
                t = np.arange(int(0.25 * rate)) / rate
                phase = 2 * np.pi * (48 * t + 6 * (1 - np.exp(-t * 28)))
                add(np.sin(phase) * np.exp(-t * 21), onset, 0.19)
            else:
                t = np.arange(int(0.19 * rate)) / rate
                noise = rng.normal(0, 1, len(t))
                brush = np.convolve(noise, np.ones(9) / 9, mode="same") * np.exp(-t * 27)
                add(brush, onset, 0.08, -0.15)
            for half in (0, 0.5):
                t = np.arange(int(0.065 * rate)) / rate
                noise = rng.normal(0, 1, len(t) + 1)
                hat = np.diff(noise) * np.exp(-t * 85)
                add(hat, onset + half * beat, 0.018 if half else 0.025, 0.4)
    fade_in = np.minimum(1, np.arange(samples) / rate / 0.8)
    fade_out = np.minimum(1, (SECONDS - np.arange(samples) / rate) / 2)
    mix *= (fade_in * fade_out)[:, None]
    mix *= 0.85 / max(1, np.max(np.abs(mix)))
    with wave.open(str(path), "wb") as output:
        output.setnchannels(2)
        output.setsampwidth(2)
        output.setframerate(rate)
        output.writeframes((mix * 32767).astype("<i2").tobytes())


def main():
    for command in ("ffmpeg", "ffprobe"):
        if not shutil.which(command):
            raise RuntimeError(f"{command} is required on PATH.")
    MEDIA.mkdir(parents=True, exist_ok=True)
    output = MEDIA / "localmorph-windows-launch.mp4"
    frame(3.5).save(MEDIA / "localmorph-windows-poster.webp", quality=88, method=6)
    contact = Image.new("RGB", (960, 270 * 3), PAPER)
    for index, time in enumerate((3.5, 8, 14, 20, 26, 29)):
        tile = frame(time).resize((480, 270), Image.Resampling.LANCZOS)
        contact.paste(tile, ((index % 2) * 480, (index // 2) * 270))
    evidence = ROOT / "artifacts" / "desktop-launch"
    evidence.mkdir(parents=True, exist_ok=True)
    contact.save(evidence / "storyboard.jpg", quality=92)

    with tempfile.TemporaryDirectory(prefix="localmorph-launch-") as temporary:
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
            "-metadata", "title=LocalMorph for Windows - Launch",
            "-metadata", "comment=Actual app screenshots. Original procedurally composed instrumental.",
            str(output),
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
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(output)],
        check=True, capture_output=True, text=True,
    )
    metadata = json.loads(probe.stdout)
    video = next(stream for stream in metadata["streams"] if stream["codec_type"] == "video")
    audio = next(stream for stream in metadata["streams"] if stream["codec_type"] == "audio")
    assert (video["width"], video["height"]) == (WIDTH, HEIGHT)
    assert video["codec_name"] == "h264" and video["pix_fmt"] == "yuv420p"
    assert video["r_frame_rate"] == "30/1" and int(video["nb_frames"]) == FPS * SECONDS
    assert abs(float(metadata["format"]["duration"]) - SECONDS) < 0.1
    assert audio["codec_name"] == "aac" and audio["channels"] == 2
    (evidence / "media-probe.json").write_text(probe.stdout, encoding="utf-8")
    print(f"Verified: {output} ({output.stat().st_size / 1_000_000:.1f} MB)", flush=True)


if __name__ == "__main__":
    main()
