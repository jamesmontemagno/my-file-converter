"""Exercise cache miss, hit, corruption and input invalidation without Apple tools."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "mac"))
import media_cache as cache
from store_release import DEPENDENCY_LICENSES


class MediaCacheTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name) / "cache"
        self.directory.mkdir()
        self.expected = {"schema": 1, "xcode": "Xcode fixture", "sdk_build": "fixture"}
        self.pins = {name: hashlib.sha256(name.encode()).hexdigest()
                     for name in cache.source_pins()}
        self.pin_patch = patch.object(cache, "source_pins", return_value=self.pins)
        self.pin_patch.start()
        self.addCleanup(self.pin_patch.stop)
        for name in self.pins:
            path = self.directory / name if name == cache.ARCHIVE else self.directory / "media/sources" / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(name.encode())
        self.media = self.directory / "media/arm64"
        for name in cache.METADATA:
            (self.media / name).parent.mkdir(parents=True, exist_ok=True)
            (self.media / name).write_text("fixture")
        (self.media / "xcode.txt").write_text(self.expected["xcode"] + "\n")
        for name in ("bin/ffmpeg", "bin/ffprobe", *("licenses/" + name for name in DEPENDENCY_LICENSES)):
            path = self.media / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("fixture")
        self.seal()

    def seal(self):
        (self.directory / "cache.json").write_text(json.dumps({
            "context": self.expected, "producer_commit": "a" * 40,
            "files": cache.inventory(self.directory),
        }))

    def test_hit_checks_exact_inventory_and_all_source_pins(self):
        manifest = cache.verify_inventory(self.directory, self.expected)
        self.assertEqual("a" * 40, manifest["producer_commit"])
        for name in self.pins:
            with self.subTest(source=name):
                path = self.directory / name if name == cache.ARCHIVE else self.directory / "media/sources" / name
                original = path.read_bytes()
                path.write_bytes(b"corrupt archive")
                self.seal()
                with self.assertRaisesRegex(ValueError, "source does not match its pin"):
                    cache.verify_inventory(self.directory, self.expected)
                path.write_bytes(original)
                self.seal()

    def test_corrupt_missing_or_unexpected_files_fail_closed(self):
        tool = self.media / "bin/ffmpeg"
        tool.write_text("changed")
        with self.assertRaisesRegex(ValueError, "inventory/hash mismatch"):
            cache.verify_inventory(self.directory, self.expected)
        tool.unlink()
        with self.assertRaisesRegex(ValueError, "inventory/hash mismatch"):
            cache.verify_inventory(self.directory, self.expected)
        tool.write_text("fixture")
        (self.directory / "certificate.p12").write_text("not a credential")
        self.seal()
        with self.assertRaisesRegex(ValueError, "Unexpected or missing"):
            cache.verify_inventory(self.directory, self.expected)

    def test_inputs_toolchain_sdk_os_architecture_and_legal_changes_invalidate(self):
        for field in ("inputs", "sources", "environment", "output", "os", "kernel",
                      "architecture", "runner", "xcode", "compiler", "sdk_version",
                      "sdk_build", "sdk_settings", "tools"):
            with self.subTest(field=field):
                changed = {**self.expected, field: "changed"}
                self.assertNotEqual(hashlib.sha256(cache.canonical(self.expected)).hexdigest(),
                                    hashlib.sha256(cache.canonical(changed)).hexdigest())
                with self.assertRaisesRegex(ValueError, "inputs/toolchain do not match"):
                    cache.verify_inventory(self.directory, changed)

    def test_missing_producer_commit_is_rejected(self):
        manifest = json.loads((self.directory / "cache.json").read_text())
        manifest["producer_commit"] = "unknown"
        (self.directory / "cache.json").write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, "producer commit"):
            cache.verify_inventory(self.directory, self.expected)

    def tool_output(self, *args):
        if args[:3] == ("xcrun", "vtool", "-show-build"):
            return "platform MACOS\nminos 15.0\nsdk 27.0"
        return " TS subtitles V->V\n TS ass V->V\n TS zscale V->V"

    def test_hit_reruns_native_validation_not_just_checksums(self):
        with patch.object(cache, "validate_media") as validate, \
                patch.object(cache, "command", side_effect=self.tool_output) as commands, \
                patch.object(cache.subprocess, "run") as signature:
            signature.return_value.returncode = 0
            signature.return_value.stdout = ""
            signature.return_value.stderr = "Signature=adhoc\nTeamIdentifier=not set\n"
            cache.verify(self.directory, self.expected)
            validate.assert_called_once_with(self.media)
            self.assertEqual(2, signature.call_count)
            self.assertEqual(3, commands.call_count)
            signature.return_value.stderr = "Authority=Developer ID\nTeamIdentifier=ABC"
            with self.assertRaisesRegex(ValueError, "Identity-signed"):
                cache.verify(self.directory, self.expected)

    def test_wrong_deployment_target_and_missing_filter_fail(self):
        for output, message in (
            ("platform MACOS\nminos 26.0", "macOS 15.0"),
            ("platform IOS\nminos 15.0", "macOS 15.0"),
        ):
            with patch.object(cache, "validate_media"), patch.object(cache, "command", return_value=output):
                with self.assertRaisesRegex(ValueError, message):
                    cache.verify(self.directory, self.expected)
        with patch.object(cache, "validate_media"), \
                patch.object(cache, "command", side_effect=lambda *args:
                             self.tool_output(*args) if args[0] == "xcrun" else " TS subtitles V->V"), \
                patch.object(cache.subprocess, "run") as signature:
            signature.return_value.returncode = 1
            signature.return_value.stdout = ""
            signature.return_value.stderr = "code object is not signed at all"
            with self.assertRaisesRegex(ValueError, "required filters"):
                cache.verify(self.directory, self.expected)

    def test_miss_builds_from_source_exports_only_allowed_files_and_verifies(self):
        output = Path(self.temporary.name) / "new-cache"
        environment = {"PATH": "restricted"}
        expected = {**self.expected, "environment": environment}

        def build_command(args, **kwargs):
            self.assertTrue(kwargs["check"])
            self.assertEqual(environment, kwargs["env"])
            if args[0] == "curl":
                Path(args[-1]).write_bytes(cache.ARCHIVE.encode())
            else:
                self.assertEqual("bash", args[0])
                shutil.copytree(self.directory / "media", Path(args[-2]))
                (Path(args[-2]) / "arm64/deps-src").mkdir()
                (Path(args[-2]) / "arm64/deps-src/not-cached").write_text("build tree")

        with patch.object(cache, "CACHE", output), \
                patch.dict(os.environ, {"RUNNER_TEMP": self.temporary.name}), \
                patch.object(cache.subprocess, "run", side_effect=build_command) as build, \
                patch.object(cache, "command", return_value="b" * 40), \
                patch.object(cache, "verify") as verify:
            cache.build(expected)
            self.assertEqual(2, build.call_count)
            verify.assert_called_once_with(output, expected)
            cache.verify_inventory(output, expected)
            self.assertFalse((output / "media/arm64/deps-src").exists())
            self.assertIn(str(output / "media/arm64/bin/ffmpeg"),
                          (output / "media/arm64/binaries.sha256").read_text())
            with self.assertRaisesRegex(ValueError, "overwrite"):
                cache.build(expected)

    def test_context_covers_scripts_patches_licenses_runner_and_selected_tools(self):
        root = Path(self.temporary.name) / "repository"
        inputs = (
            "scripts/mac/build-proof-media.sh", "scripts/mac/component.patch",
            "dotnet/packaging/mac/ThirdPartyNotices.txt", ".github/actions/mac-media/action.yml",
        )
        for name in inputs:
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(name)
        # Native tools are not available on Windows; their discovery/content is mocked here.
        def native(*args):
            if args == ("uname", "-s"):
                return "Darwin"
            if args == ("uname", "-m"):
                return "arm64"
            if args[:2] == ("xcrun", "--find"):
                return str(Path(__file__).resolve())
            return "toolchain fixture"

        with patch.object(cache, "ROOT", root), \
                patch.object(cache, "build_environment", return_value={"SDKROOT": "/fixture/sdk"}), \
                patch.object(cache, "command", side_effect=native), \
                patch.object(cache, "digest", side_effect=lambda path: str(path)), \
                patch.object(Path, "resolve", return_value=Path(__file__)), \
                patch.dict(os.environ, {"ImageVersion": "fixture-image"}):
            expected = cache.context()
        self.assertEqual(set(inputs), set(expected["inputs"]))
        self.assertEqual("fixture-image", expected["runner"]["ImageVersion"])
        self.assertEqual({"clang", "clang++", "ld", "ar", "ranlib", "strip",
                          "make", "bash", "tar", "shasum", "curl"}, set(expected["tools"]))
        self.assertIn("SDKSettings.plist", expected["sdk_settings"])
        self.assertEqual(set(self.pins), set(expected["sources"]))

    def test_workflows_share_exact_cache_before_signing_and_keep_baseline_layout(self):
        action = (cache.ROOT / ".github/actions/mac-media/action.yml").read_text()
        self.assertNotIn("restore-keys:", action)
        self.assertNotIn("actions/cache@", action)
        self.assertLess(action.index("media_cache.py verify"), action.index("actions/cache/save@"))
        for name in ("bridge-ci.yml", "mac-store-publish.yml"):
            workflow = (cache.ROOT / ".github/workflows" / name).read_text()
            self.assertEqual(1, workflow.count("uses: ./.github/actions/mac-media"))
            self.assertLess(workflow.index("uses: ./.github/actions/mac-media"),
                            workflow.index("codesign" if name == "bridge-ci.yml" else "id: signing"))
        bridge = (cache.ROOT / ".github/workflows/bridge-ci.yml").read_text()
        upload = bridge.split("name: mac-sandbox-app\n", 1)[1].split("      - name:", 1)[0]
        self.assertNotIn("artifacts/mac-media", upload)
        self.assertIn("artifacts/mac-proof/sandbox-app.zip", upload)
        self.assertIn("desktop-app-mac-baseline:", bridge)


if __name__ == "__main__":
    unittest.main()
