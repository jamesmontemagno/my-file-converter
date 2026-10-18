"""Validate the Mac build's effective scene, OS and architecture contract."""

import argparse
from pathlib import Path
import plistlib
import subprocess


def validate_scene_manifest(info):
    manifest = info.get("UIApplicationSceneManifest", {})
    if manifest.get("UIApplicationSupportsMultipleScenes") is not False:
        raise ValueError("The app must explicitly use single-window scenes.")
    configurations = manifest.get("UISceneConfigurations", {}).get(
        "UIWindowSceneSessionRoleApplication", []
    )
    if len(configurations) != 1 or configurations[0] != {
        "UISceneConfigurationName": "__MAUI_DEFAULT_SCENE_CONFIGURATION__",
        "UISceneDelegateClassName": "SceneDelegate",
    }:
        raise ValueError("The MAUI scene configuration and registered delegate must match.")


def validate_app(app):
    with (app / "Contents/Info.plist").open("rb") as source:
        info = plistlib.load(source)
    validate_scene_manifest(info)
    minimum = str(info.get("LSMinimumSystemVersion", ""))
    if minimum not in ("15.0", "15.0.0"):
        raise ValueError(f"Expected minimum macOS 15.0; found {minimum!r}.")
    executable = info.get("CFBundleExecutable")
    if not executable or Path(executable).name != executable:
        raise ValueError("Missing or invalid CFBundleExecutable.")
    architectures = subprocess.run(
        ["lipo", "-archs", str(app / "Contents/MacOS" / executable)],
        check=True, capture_output=True, text=True,
    ).stdout.strip()
    if architectures != "arm64":
        raise ValueError(f"Expected Apple Silicon only; found {architectures!r}.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=Path)
    arguments = parser.parse_args()
    try:
        validate_app(arguments.app)
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Mac app validation failed: {error}\n")
    print("Verified Apple Silicon-only app, macOS 15+ and MAUI single-window scenes.")
