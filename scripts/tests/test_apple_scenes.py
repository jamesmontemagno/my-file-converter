"""Scene adoption and effective Mac bundle regression checks."""

import copy
from pathlib import Path
import plistlib
import runpy
import subprocess
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "dotnet/src/LocalMorph.App"
VALIDATOR = runpy.run_path(str(ROOT / "scripts/mac/validate_app.py"))
validate_scene_manifest = VALIDATOR["validate_scene_manifest"]
validate_app = VALIDATOR["validate_app"]


class AppleSceneTests(unittest.TestCase):
    def scene_info(self):
        with (APP / "Platforms/MacCatalyst/Info.plist").open("rb") as source:
            return plistlib.load(source)

    def test_both_apple_heads_register_matching_maui_scenes(self):
        for platform in ("iOS", "MacCatalyst"):
            with self.subTest(platform=platform):
                directory = APP / "Platforms" / platform
                with (directory / "Info.plist").open("rb") as source:
                    validate_scene_manifest(plistlib.load(source))
                delegate = (directory / "SceneDelegate.cs").read_text()
                self.assertIn('[Register("SceneDelegate")]', delegate)
                self.assertIn("class SceneDelegate : MauiUISceneDelegate", delegate)
                self.assertNotIn("CreateWindow", delegate)

    def test_missing_or_mismatched_scene_configuration_is_rejected(self):
        original = self.scene_info()
        missing = copy.deepcopy(original)
        del missing["UIApplicationSceneManifest"]
        renamed = copy.deepcopy(original)
        renamed["UIApplicationSceneManifest"]["UISceneConfigurations"][
            "UIWindowSceneSessionRoleApplication"
        ][0]["UISceneDelegateClassName"] = "OtherDelegate"
        multiwindow = copy.deepcopy(original)
        multiwindow["UIApplicationSceneManifest"]["UIApplicationSupportsMultipleScenes"] = True
        for info in (missing, renamed, multiwindow):
            with self.subTest(info=info), self.assertRaises(ValueError):
                validate_scene_manifest(info)

    def test_bundle_validator_checks_actual_minimum_and_executable_architecture(self):
        with tempfile.TemporaryDirectory() as directory:
            app = Path(directory) / "LocalMorph.app"
            (app / "Contents/MacOS").mkdir(parents=True)
            info = self.scene_info()
            info.update(LSMinimumSystemVersion="15.0", CFBundleExecutable="LocalMorph")
            with (app / "Contents/Info.plist").open("wb") as output:
                plistlib.dump(info, output)
            with patch("subprocess.run", return_value=subprocess.CompletedProcess(
                [], 0, stdout="arm64\n"
            )) as lipo:
                validate_app(app)
                self.assertEqual(lipo.call_args.args[0], [
                    "lipo", "-archs", str(app / "Contents/MacOS/LocalMorph"),
                ])
            for architectures in ("x86_64", "x86_64 arm64", ""):
                with self.subTest(architectures=architectures), patch(
                    "subprocess.run", return_value=subprocess.CompletedProcess(
                        [], 0, stdout=architectures,
                    )
                ), self.assertRaisesRegex(ValueError, "Apple Silicon only"):
                    validate_app(app)
            for minimum in ("12.0", "14.0", "", "18.0"):
                info["LSMinimumSystemVersion"] = minimum
                with (app / "Contents/Info.plist").open("wb") as output:
                    plistlib.dump(info, output)
                with self.subTest(minimum=minimum), self.assertRaisesRegex(
                    ValueError, "minimum macOS 15.0"
                ):
                    validate_app(app)

    def test_missing_bundle_fails_explicitly(self):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run(
                ["python3", str(ROOT / "scripts/mac/validate_app.py"),
                 str(Path(directory) / "missing.app")],
                capture_output=True, text=True,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("Mac app validation failed", result.stderr)


if __name__ == "__main__":
    unittest.main()
