"""Non-signing checks for development-only Mac sandbox proof tooling."""

import os
from pathlib import Path
import plistlib
import re
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


class MacProofTests(unittest.TestCase):
    def media_download(self, mode):
        builder = ROOT / "scripts/mac/build-proof-media.sh"
        script = builder.read_text()
        source_hash = re.search(r'^SHA256="([0-9a-f]{64})"$', script, re.MULTILINE)[1]
        pins = re.findall(r'^\s*"([^|"]+)\|[^|"]+\|([0-9a-f]{64})(?:\|[^"]*)?"',
                          script, re.MULTILINE)
        self.assertEqual(6, len(pins))
        hash_cases = "\n".join(f'  {name}|{name}.partial) echo {digest} ;;'
                               for name, digest in pins)
        with tempfile.TemporaryDirectory() as directory:
            temporary = Path(directory)
            source = temporary / "source.tar.xz"
            source.write_bytes(b"source fixture")
            output = temporary / "output"
            log = temporary / "downloads.txt"
            for name, body in (
                ("uname", "echo Darwin"),
                ("shasum", f"""\
case "$MOCK_DOWNLOAD_MODE" in
  corrupt*) case "$3" in *freetype*) echo invalid-hash; exit 0 ;; esac ;;
esac
case "${{3##*/}}" in
  source.tar.xz) echo {source_hash} ;;
{hash_cases}
  *) exit 1 ;;
esac"""),
                ("curl", """\
while [ "$#" -gt 0 ]; do
  case "$1" in
    -o) shift; output="$1" ;;
    https://*) url="$1" ;;
  esac
  shift
done
printf '%s\\n' "$url" >> "$MOCK_DOWNLOAD_LOG"
printf fixture > "$output"
case "$url" in
  *freetype*)
    if [ "$MOCK_DOWNLOAD_MODE" = all-fail ]; then exit 22; fi
    case "$url" in
      *download-mirror.savannah.gnu.org*)
        case "$MOCK_DOWNLOAD_MODE" in primary-fails|corrupt-fallback) exit 22 ;; esac ;;
    esac ;;
esac"""),
                ("tar", 'echo DOWNLOADS_VERIFIED >&2; exit 23'),
            ):
                executable = temporary / name
                executable.write_text("#!/bin/sh\n" + body + "\n")
                executable.chmod(0o755)
            result = subprocess.run(
                ["bash", str(builder), str(source), str(output), "arm64"],
                env={**os.environ, "PATH": f"{temporary}{os.pathsep}{os.environ['PATH']}",
                     "MOCK_DOWNLOAD_MODE": mode, "MOCK_DOWNLOAD_LOG": str(log)},
                capture_output=True, text=True,
            )
            self.assertTrue(log.is_file(), result.stderr)
            attempts = [url for url in log.read_text().splitlines() if "freetype" in url]
            metadata = output / "arm64/dependency-sources.txt"
            cached = (output / "sources/freetype-2.13.3.tar.xz").exists()
            return result, attempts, metadata.read_text(), cached

    def test_media_download_uses_gnu_mirror_and_verifies_before_building(self):
        result, attempts, metadata, cached = self.media_download("success")
        self.assertEqual(23, result.returncode, result.stderr)
        self.assertIn("DOWNLOADS_VERIFIED", result.stderr)
        self.assertEqual(1, len(attempts))
        self.assertIn("https://download-mirror.savannah.gnu.org/", attempts[0])
        self.assertIn(attempts[0], metadata)
        self.assertTrue(cached)

    def test_media_download_falls_back_and_records_the_actual_source(self):
        result, attempts, metadata, cached = self.media_download("primary-fails")
        self.assertEqual(23, result.returncode, result.stderr)
        self.assertEqual(2, len(attempts))
        self.assertIn("https://downloads.sourceforge.net/", attempts[1])
        self.assertIn(attempts[1], metadata)
        self.assertNotIn(attempts[0], metadata)
        self.assertTrue(cached)

    def test_media_download_exhausted_sources_fail_without_caching(self):
        result, attempts, metadata, cached = self.media_download("all-fail")
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertEqual(2, len(attempts))
        self.assertIn("Could not download dependency freetype-2.13.3.tar.xz", result.stderr)
        self.assertNotIn("DOWNLOADS_VERIFIED", result.stderr)
        self.assertNotIn("freetype", metadata)
        self.assertFalse(cached)

    def test_media_download_checksum_failure_is_not_retried_or_cached(self):
        for mode, count in (("corrupt", 1), ("corrupt-fallback", 2)):
            with self.subTest(mode=mode):
                result, attempts, metadata, cached = self.media_download(mode)
                self.assertEqual(1, result.returncode, result.stderr)
                self.assertEqual(count, len(attempts))
                self.assertIn("Dependency source freetype-2.13.3.tar.xz does not match its pin.", result.stderr)
                self.assertNotIn("DOWNLOADS_VERIFIED", result.stderr)
                self.assertNotIn("freetype", metadata)
                self.assertFalse(cached)

    def test_proof_entitlements_are_isolated_and_helpers_inherit(self):
        directory = ROOT / "dotnet/src/LocalMorph.App/Platforms/MacCatalyst"
        with (directory / "SandboxProof.entitlements").open("rb") as source:
            self.assertEqual(plistlib.load(source), {
                "com.apple.security.app-sandbox": True,
                "com.apple.security.files.user-selected.read-write": True,
                "com.apple.security.files.bookmarks.app-scope": True,
            })
        with (directory / "MediaTool.entitlements").open("rb") as source:
            self.assertEqual(plistlib.load(source), {
                "com.apple.security.app-sandbox": True,
                "com.apple.security.inherit": True,
            })

    def test_corrupt_source_is_rejected_before_building(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source.tar.xz"
            source.write_bytes(b"not the pinned FFmpeg source")
            output = Path(directory) / "output"
            result = subprocess.run(
                ["bash", str(ROOT / "scripts/mac/build-proof-media.sh"), str(source), str(output), "arm64"],
                capture_output=True, text=True,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("does not match", result.stderr)
            self.assertFalse(output.exists())

    def test_dependency_check_ignores_tool_headers_but_rejects_external_libraries(self):
        for dependency, passes in (
            ("/usr/lib/libSystem.B.dylib", True),
            ("/System/Library/Frameworks/VideoToolbox.framework/Versions/A/VideoToolbox", True),
            ("/opt/homebrew/lib/libx264.dylib", False),
            ("/usr/local/lib/libass.dylib", False),
            ("@rpath/libavcodec.dylib", False),
            ("/Users/developer/lib/libzimg.dylib", False),
        ):
            with self.subTest(dependency=dependency), tempfile.TemporaryDirectory() as directory:
                temporary = Path(directory)
                for name, body in (
                    ("shasum", "exit 0"),
                    ("lipo", "echo arm64"),
                    ("otool", 'printf "%s:\\n\\t%s (compatibility version 1.0.0)\\n" "$2" "$PROOF_TEST_DEPENDENCY"'),
                    ("dotnet", 'echo "MOCK_BUILD_REACHED" >&2; exit 23'),
                ):
                    executable = temporary / name
                    executable.write_text("#!/bin/sh\n" + body + "\n")
                    executable.chmod(0o755)
                result = subprocess.run(
                    ["bash", str(ROOT / "scripts/mac/prepare-sandbox-proof.sh"),
                     str(temporary), str(temporary / "output")],
                    env={**os.environ, "PATH": f"{temporary}{os.pathsep}{os.environ['PATH']}",
                         "PROOF_TEST_DEPENDENCY": dependency},
                    capture_output=True, text=True,
                )
                self.assertEqual("MOCK_BUILD_REACHED" in result.stderr, passes, result.stdout + result.stderr)
                self.assertNotEqual(result.returncode, 0)
                if not passes:
                    self.assertFalse((temporary / "output").exists())

    def test_failed_dependency_inspection_blocks_build(self):
        with tempfile.TemporaryDirectory() as directory:
            temporary = Path(directory)
            for name, body in (
                ("shasum", "exit 0"),
                ("lipo", "echo arm64"),
                ("otool", "echo 'INSPECTION_FAILED' >&2; exit 24"),
                ("dotnet", "echo 'UNEXPECTED_BUILD' >&2; exit 23"),
            ):
                executable = temporary / name
                executable.write_text("#!/bin/sh\n" + body + "\n")
                executable.chmod(0o755)
            result = subprocess.run(
                ["bash", str(ROOT / "scripts/mac/prepare-sandbox-proof.sh"),
                 str(temporary), str(temporary / "output")],
                env={**os.environ, "PATH": f"{temporary}{os.pathsep}{os.environ['PATH']}"},
                capture_output=True, text=True,
            )
            self.assertEqual(result.returncode, 24)
            self.assertNotIn("UNEXPECTED_BUILD", result.stderr)
            self.assertFalse((temporary / "output").exists())

    def test_existing_proof_output_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run(
                ["bash", str(ROOT / "scripts/mac/prepare-sandbox-proof.sh"), directory, directory],
                capture_output=True, text=True,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("Refusing to overwrite", result.stderr)

    def test_intel_source_build_is_rejected(self):
        result = subprocess.run(
            ["bash", str(ROOT / "scripts/mac/build-proof-media.sh"),
             "unused-source", "unused-output", "x86_64"],
            capture_output=True, text=True,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("<arm64>", result.stderr)

    def test_universal_and_intel_tools_are_rejected_before_building(self):
        for architectures in ("x86_64", "x86_64 arm64", ""):
            with self.subTest(architectures=architectures), tempfile.TemporaryDirectory() as directory:
                temporary = Path(directory)
                for name, body in (
                    ("shasum", "exit 0"),
                    ("lipo", 'printf "%s\\n" "$PROOF_TEST_ARCHITECTURES"'),
                    ("dotnet", "echo 'UNEXPECTED_BUILD' >&2; exit 23"),
                ):
                    executable = temporary / name
                    executable.write_text("#!/bin/sh\n" + body + "\n")
                    executable.chmod(0o755)
                result = subprocess.run(
                    ["bash", str(ROOT / "scripts/mac/prepare-sandbox-proof.sh"),
                     str(temporary), str(temporary / "output")],
                    env={**os.environ, "PATH": f"{temporary}{os.pathsep}{os.environ['PATH']}",
                         "PROOF_TEST_ARCHITECTURES": architectures},
                    capture_output=True, text=True,
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("Apple Silicon only", result.stderr)
                self.assertNotIn("UNEXPECTED_BUILD", result.stderr)
                self.assertFalse((temporary / "output").exists())


if __name__ == "__main__":
    unittest.main()
