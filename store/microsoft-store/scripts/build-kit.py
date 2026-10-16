import hashlib
import io
import json
import subprocess
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


KIT = Path(__file__).resolve().parents[1]
REPO = Path(__file__).resolve().parents[3]
FONTS = REPO / "dotnet" / "src" / "LocalMorph.App" / "Resources" / "Fonts"
APP_ICON = REPO / "dotnet" / "src" / "LocalMorph.App" / "Resources" / "AppIcon"


def font(size, semibold=False):
    name = "OpenSans-Semibold.ttf" if semibold else "OpenSans-Regular.ttf"
    return ImageFont.truetype(str(FONTS / name), size)


def rasterize(path, size):
    result = subprocess.run(
        ["magick", "-background", "none", "-density", str(max(96, size * 96 // 456 + 1)),
         str(path), "-resize", f"{size}x{size}", "png:-"],
        check=True, capture_output=True,
    )
    return Image.open(io.BytesIO(result.stdout)).convert("RGBA")


def validate_copy(listing, shots):
    assert len(listing["description"]) <= 10000
    assert len(listing["shortDescription"]) <= 270
    assert len(listing["whatsNewForExistingListing"]) <= 1500
    assert len(listing["productFeatures"]) <= 20
    assert all(len(feature) <= 200 for feature in listing["productFeatures"])
    assert len(shots) <= 10
    assert len({shot["file"] for shot in shots}) == len(shots)
    assert all(len(shot["caption"]) <= 200 for shot in shots)


def main():
    listing = json.loads((KIT / "en-us" / "listing.json").read_text(encoding="utf-8"))
    shots = json.loads((KIT / "en-us" / "screenshots.json").read_text(encoding="utf-8"))
    validate_copy(listing, shots)
    logos = KIT / "logos"
    promotional = KIT / "promotional"
    logos.mkdir(exist_ok=True)
    promotional.mkdir(exist_ok=True)
    for size in (300, 512, 1024):
        tile = rasterize(APP_ICON / "appicon.svg", size)
        tile.alpha_composite(rasterize(APP_ICON / "appiconfg.svg", size))
        tile.convert("RGB").save(logos / f"app-tile-{size}.png", optimize=True)
    hero = Image.new("RGB", (1920, 1080), "#0B172A")
    mark = rasterize(APP_ICON / "appiconfg.svg", 2100)
    hero.paste(mark, (-90, -580), mark)
    hero.save(logos / "super-hero-1920x1080.png", optimize=True)
    contact = Image.new("RGB", (1920, ((len(shots) + 1) // 2) * 590), "#F3F5F9")
    contact_draw = ImageDraw.Draw(contact)
    for index, shot in enumerate(shots):
        path = KIT / "en-us" / "screenshots" / shot["file"]
        with Image.open(path) as source:
            screenshot = source.convert("RGB")
        assert screenshot.width >= 1366 and screenshot.height >= 768, path
        assert screenshot.width <= 3840 and screenshot.height <= 2160, path
        assert path.stat().st_size < 50 * 1024 * 1024, path
        canvas = Image.new("RGB", (2560, 1800), "#F3F5F9")
        draw = ImageDraw.Draw(canvas)
        draw.text((96, 80), shot["headline"], font=font(86, True), fill="#0B172A")
        draw.text((100, 210), shot["subheading"], font=font(32), fill="#46566F")
        tile = Image.open(logos / "app-tile-300.png").resize((96, 96), Image.Resampling.LANCZOS)
        canvas.paste(tile, (2368, 90))
        screenshot.thumbnail((2368, 1332), Image.Resampling.LANCZOS)
        canvas.paste(screenshot, ((2560 - screenshot.width) // 2, 360))
        canvas.save(promotional / shot["file"], optimize=True)
        thumbnail = screenshot.copy()
        thumbnail.thumbnail((896, 504), Image.Resampling.LANCZOS)
        x = 32 + (index % 2) * 960
        y = 22 + (index // 2) * 590
        contact_draw.text((x, y), f"{index + 1:02}  {shot['headline']}", font=font(26, True), fill="#0B172A")
        contact.paste(thumbnail, (x, y + 56))
    contact.save(KIT / "contact-sheet.png", optimize=True)
    copy = [
        listing["productName"], "", "SHORT DESCRIPTION", listing["shortDescription"],
        "", "DESCRIPTION", listing["description"], "", "FEATURES",
        *listing["productFeatures"], "", "FIRST SUBMISSION: Leave What's new blank.",
        "", "UPDATE DRAFT", listing["whatsNewForExistingListing"],
    ]
    (KIT / "en-us" / "listing-copy.txt").write_text("\n".join(copy) + "\n", encoding="utf-8")
    inventory = []
    for path in sorted(KIT.rglob("*.png")):
        with Image.open(path) as image:
            dimensions = [image.width, image.height]
        inventory.append({
            "file": str(path.relative_to(KIT)), "dimensions": dimensions,
            "bytes": path.stat().st_size,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        })
    (KIT / "assets.json").write_text(json.dumps({
        "requirementsChecked": "2026-10-16",
        "sourceImage": "Original AI-generated coastal demo photograph; no application UI was generated.",
        "screenshots": "Actual Windows app captures; promotional versions are not Store screenshots.",
        "captureNormalization": {
            "03-preview-trim-tracks.png": "Extracted the intact 2560x1440 main-window capture from winapp's multi-window diagnostic composition; removed only its 28px label strip and adjacent tooltip panel."
        },
        "metadataCharacters": {
            "description": len(listing["description"]),
            "shortDescription": len(listing["shortDescription"]),
            "features": [len(feature) for feature in listing["productFeatures"]],
            "captions": [len(shot["caption"]) for shot in shots],
        },
        "images": inventory,
    }, indent=2) + "\n", encoding="utf-8")
    print(f"Validated {len(shots)} Store captures and generated logos, promotional images, copy, and inventory.")


if __name__ == "__main__":
    main()
