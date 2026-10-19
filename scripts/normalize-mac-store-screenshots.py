"""Flatten and color-manage genuine Mac captures for App Store delivery."""

import hashlib
import os
import tempfile
from io import BytesIO
from pathlib import Path

from PIL import Image, ImageCms


ROOT = Path(__file__).resolve().parents[1]
SCREENSHOTS = ROOT / "store" / "mac-app-store" / "en-us" / "screenshots"
CAPTURES = (
    "01-local-workspace.png",
    "02-video-presets.png",
    "03-conversion-complete.png",
    "04-conversion-history.png",
    "05-photo-conversion.png",
)
SIZE = (2560, 1600)
MATTE = (14, 17, 22, 255)


def normalize(path, srgb):
    with Image.open(path) as source:
        if source.size != SIZE:
            raise ValueError(f"{path.name} is {source.size}, expected {SIZE}.")
        source_profile = source.info.get("icc_profile")
        if not source_profile:
            raise ValueError(f"{path.name} has no embedded display color profile.")
        rgba = source.convert("RGBA")
        flattened = Image.alpha_composite(Image.new("RGBA", rgba.size, MATTE), rgba).convert("RGB")
        converted = ImageCms.profileToProfile(
            flattened,
            ImageCms.ImageCmsProfile(BytesIO(source_profile)),
            srgb,
            outputMode="RGB",
        )
        with tempfile.NamedTemporaryFile(dir=path.parent, suffix=".png", delete=False) as temporary:
            temporary_path = Path(temporary.name)
        try:
            converted.save(temporary_path, format="PNG", icc_profile=srgb.tobytes(), optimize=True)
            with Image.open(temporary_path) as check:
                if check.size != SIZE or check.mode != "RGB" or not check.info.get("icc_profile"):
                    raise ValueError(f"{path.name} failed the opaque sRGB export check.")
            os.replace(temporary_path, path)
        finally:
            temporary_path.unlink(missing_ok=True)
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    print(f"{path.relative_to(ROOT)}  2560x1600 RGB/sRGB  sha256:{digest}")


def main():
    srgb = ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB"))
    for name in CAPTURES:
        path = SCREENSHOTS / name
        if not path.is_file():
            raise FileNotFoundError(path)
        normalize(path, srgb)


if __name__ == "__main__":
    main()
