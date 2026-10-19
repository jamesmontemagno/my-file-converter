"""Build App Store marketing layouts around the genuine Mac app captures."""

import hashlib
import json
import os
import tempfile
from functools import lru_cache
from io import BytesIO
from pathlib import Path

import numpy as np
from PIL import Image, ImageCms, ImageDraw, ImageFilter, ImageFont, ImageColor


ROOT = Path(__file__).resolve().parents[1]
STORE = ROOT / "store" / "mac-app-store" / "en-us"
SCREENSHOTS = STORE / "screenshots"
OUTPUT = SCREENSHOTS / "marketing"
LOGOS = ROOT / "store" / "microsoft-store" / "logos"
FONT_DIR = ROOT / "dotnet" / "src" / "LocalMorph.App" / "Resources" / "Fonts"
MANIFEST = STORE / "asset-manifest.json"

WIDTH, HEIGHT = 2560, 1600
BACKGROUND_TOP = "#091326"
BACKGROUND_BOTTOM = "#142641"
PAPER = "#F3F6FC"
MUTED = "#A5B3C8"
BLUE = "#3978FF"
GREEN = "#B2E86B"
PANEL = "#15233A"
LINE = "#34435E"

SLIDES = (
    {
        "source": "01-local-workspace.png",
        "output": "01-your-files-your-mac.png",
        "headline": ("Your files.", "Your Mac."),
        "copy": "Convert video, audio, photos and documents right on your Mac.",
        "chips": ("NO CLOUD UPLOADS", "NO EXTRA TOOLS"),
        "motif": ("VIDEO", "AUDIO", "PHOTOS"),
        "accent": BLUE,
    },
    {
        "source": "02-video-presets.png",
        "output": "02-set-a-size-target.png",
        "headline": ("Set a target.", "Keep control."),
        "copy": "Choose a file-size target before you convert. Your originals stay untouched.",
        "chips": ("SIZE TARGETS", "ORIGINALS SAFE"),
        "motif": ("SET", "REVIEW", "DONE"),
        "accent": GREEN,
    },
    {
        "source": "03-conversion-complete.png",
        "output": "03-progress-you-can-follow.png",
        "headline": ("Progress you", "can follow."),
        "copy": "Track each job, review completed work and cancel when you need to.",
        "chips": ("BATCH PROGRESS", "CANCEL ANY JOB"),
        "motif": ("QUEUED", "RUNNING", "COMPLETE"),
        "accent": BLUE,
    },
    {
        "source": "04-conversion-history.png",
        "output": "04-your-conversions-in-history.png",
        "headline": ("Recent work.", "Right at hand."),
        "copy": "Reopen, reveal or repeat a completed conversion from History.",
        "chips": ("REOPEN", "REVEAL", "REPEAT"),
        "motif": ("FIND", "OPEN", "REPEAT"),
        "accent": GREEN,
    },
    {
        "source": "05-photo-conversion.png",
        "output": "05-photos-in-the-right-format.png",
        "headline": ("The right format.", "No detour."),
        "copy": "Preview a photo, choose an output and convert in one workspace.",
        "chips": ("JPEG", "PNG", "AVIF"),
        "motif": ("PREVIEW", "CHOOSE", "CONVERT"),
        "accent": BLUE,
    },
)

CARD_X, CARD_Y, CARD_WIDTH, CARD_HEIGHT = 730, 420, 1792, 1120
TEXT_X, TEXT_WIDTH = 145, 530


@lru_cache(maxsize=64)
def font(size, semibold=False):
    name = "OpenSans-Semibold.ttf" if semibold else "OpenSans-Regular.ttf"
    return ImageFont.truetype(str(FONT_DIR / name), size)


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def make_background(accent):
    top = np.array(ImageColor.getrgb(BACKGROUND_TOP), dtype=np.float32)
    bottom = np.array(ImageColor.getrgb(BACKGROUND_BOTTOM), dtype=np.float32)
    progress = np.linspace(0, 1, HEIGHT, dtype=np.float32)[:, None, None]
    rows = top * (1 - progress) + bottom * progress
    pixels = np.broadcast_to(rows, (HEIGHT, WIDTH, 3)).astype(np.uint8)
    image = Image.fromarray(pixels).convert("RGBA")

    glow = Image.new("RGBA", image.size, (0, 0, 0, 0))
    glow_draw = ImageDraw.Draw(glow)
    rgb = ImageColor.getrgb(accent)
    glow_draw.ellipse((1080, -260, 2920, 1620), fill=(*rgb, 62))
    glow = glow.filter(ImageFilter.GaussianBlur(210))
    image = Image.alpha_composite(image, glow)

    grid = Image.new("RGBA", image.size, (0, 0, 0, 0))
    grid_draw = ImageDraw.Draw(grid)
    for x in range(860, WIDTH, 160):
        grid_draw.line((x, 115, x, HEIGHT - 55), fill=(135, 165, 220, 13), width=1)
    for y in range(180, HEIGHT, 160):
        grid_draw.line((650, y, WIDTH - 55, y), fill=(135, 165, 220, 10), width=1)
    return Image.alpha_composite(image, grid)


def app_icon():
    with Image.open(LOGOS / "app-tile-1024.png") as source:
        icon = source.convert("RGBA").resize((72, 72), Image.Resampling.LANCZOS)
    mask = Image.new("L", icon.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, 71, 71), radius=16, fill=255)
    icon.putalpha(mask)
    return icon


def output_profile():
    source_path = SCREENSHOTS / SLIDES[0]["source"]
    with Image.open(source_path) as source:
        profile_bytes = source.info.get("icc_profile")
    if not profile_bytes:
        raise ValueError(f"{source_path.name} has no embedded sRGB profile.")
    return ImageCms.ImageCmsProfile(BytesIO(profile_bytes))


def screenshot_image(filename, srgb):
    path = SCREENSHOTS / filename
    if not path.is_file():
        raise FileNotFoundError(path)

    with Image.open(path) as source:
        if source.size != (WIDTH, HEIGHT):
            raise ValueError(f"{filename} is {source.size}; expected 2560x1600.")
        source_profile = source.info.get("icc_profile")
        if source.mode != "RGB" or not source_profile:
            raise ValueError(f"{filename} must be opaque RGB with an embedded color profile.")
        converted = ImageCms.profileToProfile(
            source,
            ImageCms.ImageCmsProfile(BytesIO(source_profile)),
            srgb,
            outputMode="RGB",
        )
        return converted.resize((CARD_WIDTH, CARD_HEIGHT), Image.Resampling.LANCZOS)


def draw_header(image, index):
    icon = app_icon()
    image.alpha_composite(icon, (TEXT_X, 72))
    draw = ImageDraw.Draw(image)
    draw.text((TEXT_X + 92, 78), "LocalMorph", font=font(43, True), fill=PAPER)
    draw.text((TEXT_X + 95, 132), "PRIVATE FILE CONVERSION", font=font(18, True), fill=MUTED)

    pill = (2080, 82, 2415, 140)
    draw.rounded_rectangle(pill, radius=28, fill=PANEL, outline=LINE, width=2)
    label = f"MAC APP STORE   {index + 1:02d} / {len(SLIDES):02d}"
    bounds = draw.textbbox((0, 0), label, font=font(18, True))
    label_width = bounds[2] - bounds[0]
    draw.text((pill[0] + (pill[2] - pill[0] - label_width) / 2, 101),
              label, font=font(18, True), fill=PAPER)


def draw_headline(image, lines, accent):
    draw = ImageDraw.Draw(image)
    size = 106
    while size > 72 and max(font(size, True).getlength(line) for line in lines) > TEXT_WIDTH:
        size -= 2

    line_height = round(size * 1.18)
    y = 274
    for index, line in enumerate(lines):
        color = PAPER if index == 0 else accent
        draw.text((TEXT_X, y), line, font=font(size, True), fill=color)
        y += line_height
    return y


def wrap_words(text, selected_font, max_width):
    lines = []
    current = ""
    for word in text.split():
        candidate = word if not current else f"{current} {word}"
        if current and selected_font.getlength(candidate) > max_width:
            lines.append(current)
            current = word
        else:
            current = candidate
    if current:
        lines.append(current)
    return lines


def draw_copy(image, text):
    selected_font = font(31)
    lines = wrap_words(text, selected_font, TEXT_WIDTH)
    draw = ImageDraw.Draw(image)
    y = 570
    for line in lines:
        draw.text((TEXT_X, y), line, font=selected_font, fill=MUTED)
        y += 48
    return y


def draw_chips(image, labels, accent, top):
    draw = ImageDraw.Draw(image)
    selected_font = font(17, True)
    x = TEXT_X
    y = max(800, top + 42)
    for label in labels:
        text_width = selected_font.getlength(label)
        chip_width = round(text_width + 38)
        if x + chip_width > TEXT_X + TEXT_WIDTH:
            x = TEXT_X
            y += 62
        draw.rounded_rectangle((x, y, x + chip_width, y + 46),
                               radius=23, fill=PANEL, outline=LINE, width=2)
        draw.ellipse((x + 14, y + 17, x + 24, y + 27), fill=accent)
        draw.text((x + 32, y + 12), label, font=selected_font, fill=PAPER)
        x += chip_width + 10
    return y + 46


def draw_motif(image, labels, accent, top):
    draw = ImageDraw.Draw(image)
    y = max(1010, top + 34)
    x = TEXT_X
    widths = (126, 150, 150)
    selected_font = font(16, True)
    draw.text((x, y), "A SIMPLE FLOW, FROM START TO FINISH", font=font(15, True), fill=MUTED)
    y += 38
    for index, label in enumerate(labels):
        width = widths[index]
        draw.rounded_rectangle((x, y, x + width, y + 54),
                               radius=13, fill="#101C30", outline=LINE, width=2)
        draw.ellipse((x + 13, y + 21, x + 23, y + 31), fill=accent if index == 0 else GREEN)
        draw.text((x + 31, y + 17), label, font=selected_font, fill=PAPER)
        x += width + 9
        if index < len(labels) - 1:
            draw.line((x - 7, y + 27, x + 1, y + 27), fill=accent, width=2)


def draw_screenshot_card(image, screenshot):
    shadow = Image.new("RGBA", image.size, (0, 0, 0, 0))
    shadow_draw = ImageDraw.Draw(shadow)
    shadow_draw.rounded_rectangle(
        (CARD_X + 18, CARD_Y + 26, CARD_X + CARD_WIDTH + 18, CARD_Y + CARD_HEIGHT + 26),
        radius=30,
        fill=(0, 0, 0, 180),
    )
    shadow = shadow.filter(ImageFilter.GaussianBlur(28))
    image.alpha_composite(shadow)

    frame_draw = ImageDraw.Draw(image)
    frame_draw.rounded_rectangle(
        (CARD_X - 2, CARD_Y - 2, CARD_X + CARD_WIDTH + 2, CARD_Y + CARD_HEIGHT + 2),
        radius=26,
        fill="#13213A",
    )
    mask = Image.new("L", (CARD_WIDTH, CARD_HEIGHT), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (0, 0, CARD_WIDTH - 1, CARD_HEIGHT - 1), radius=21, fill=255,
    )
    image.paste(screenshot.convert("RGBA"), (CARD_X, CARD_Y), mask)
    ImageDraw.Draw(image).rounded_rectangle(
        (CARD_X, CARD_Y, CARD_X + CARD_WIDTH, CARD_Y + CARD_HEIGHT),
        radius=22,
        outline="#597096",
        width=2,
    )
    ImageDraw.Draw(image).line(
        (CARD_X + 36, CARD_Y + 3, CARD_X + CARD_WIDTH - 36, CARD_Y + 3),
        fill=(210, 225, 250, 95),
        width=2,
    )


def draw_footer(image):
    draw = ImageDraw.Draw(image)
    draw.line((TEXT_X, 1553, 2415, 1553), fill=LINE, width=2)
    draw.ellipse((TEXT_X, 1570, TEXT_X + 12, 1582), fill=GREEN)
    draw.text((TEXT_X + 24, 1564), "ON-DEVICE BY DESIGN", font=font(15, True), fill=MUTED)
    draw.text((2185, 1562), "LOCALMORPH FOR MAC", font=font(15, True), fill=PAPER)


def render_slide(index, slide, srgb):
    canvas = make_background(slide["accent"])
    draw_header(canvas, index)
    copy_top = draw_copy(canvas, slide["copy"])
    chip_bottom = draw_chips(canvas, slide["chips"], slide["accent"], copy_top)
    draw_headline(canvas, slide["headline"], slide["accent"])
    draw_motif(canvas, slide["motif"], slide["accent"], chip_bottom)
    screenshot = screenshot_image(slide["source"], srgb)
    draw_screenshot_card(canvas, screenshot)
    draw_footer(canvas)
    return canvas.convert("RGB")


def save_atomic(image, path, srgb):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary_path = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, suffix=".png", delete=False) as temporary:
            temporary_path = Path(temporary.name)
        image.save(temporary_path, format="PNG", optimize=True, icc_profile=srgb.tobytes())
        os.replace(temporary_path, path)
    finally:
        if temporary_path is not None:
            temporary_path.unlink(missing_ok=True)


def validate_output(path):
    with Image.open(path) as image:
        image.verify()
    with Image.open(path) as image:
        if image.size != (WIDTH, HEIGHT):
            raise ValueError(f"{path.name} is not 2560x1600.")
        if image.mode != "RGB" or not image.info.get("icc_profile"):
            raise ValueError(f"{path.name} must be opaque RGB with an embedded sRGB profile.")


def update_manifest(records):
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    manifest["marketingScreenshotRenderer"] = {
        "path": Path(__file__).relative_to(ROOT).as_posix(),
        "sha256": sha256(Path(__file__).resolve()),
    }
    manifest["marketingScreenshotTreatment"] = (
        "Each layout uses an unaltered, genuine app capture inside a promotional frame. "
        "Headlines and decorative artwork remain outside the app UI."
    )
    manifest["marketingScreenshots"] = records

    temporary_path = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w", encoding="utf-8", dir=MANIFEST.parent, suffix=".json", delete=False,
        ) as temporary:
            temporary_path = Path(temporary.name)
            json.dump(manifest, temporary, indent=2)
            temporary.write("\n")
        os.replace(temporary_path, MANIFEST)
    finally:
        if temporary_path is not None:
            temporary_path.unlink(missing_ok=True)


def main():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    srgb = output_profile()
    records = []

    for index, slide in enumerate(SLIDES):
        source_path = SCREENSHOTS / slide["source"]
        output_path = OUTPUT / slide["output"]
        image = render_slide(index, slide, srgb)
        save_atomic(image, output_path, srgb)
        validate_output(output_path)
        record = {
            "path": output_path.relative_to(ROOT).as_posix(),
            "sourceScreenshot": source_path.relative_to(ROOT).as_posix(),
            "sourceSha256": sha256(source_path),
            "headline": " ".join(slide["headline"]),
            "supportingCopy": slide["copy"],
            "width": WIDTH,
            "height": HEIGHT,
            "mode": "RGB",
            "hasIccProfile": True,
            "sha256": sha256(output_path),
        }
        records.append(record)
        print(f"{record['path']}  {WIDTH}x{HEIGHT} RGB/sRGB  sha256:{record['sha256']}")

    update_manifest(records)
    print(f"Updated asset manifest: {MANIFEST.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
