"""Regression checks for desktop distribution without invoking release services."""

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


class StoreOnlyTests(unittest.TestCase):
    def test_retired_distribution_files_are_absent(self):
        for path in (
            ".github/workflows/windows-release.yml",
            ".github/workflows/mac-release.yml",
            ".github/workflows/winget-submit.yml",
            "Casks/localmorph.rb",
            "public/appcast.xml",
            "public/appcast-windows.xml",
            "dotnet/src/LocalMorph.Core/Updates/AppcastReader.cs",
        ):
            with self.subTest(path=path):
                self.assertFalse((ROOT / path).exists())
        self.assertFalse(any((ROOT / "dotnet/packaging/winget").glob("*.yaml")))

    def test_store_and_bridge_delivery_are_preserved(self):
        for path in (
            ".github/workflows/windows-store-publish.yml",
            ".github/workflows/bridge-release.yml",
            "scripts/windows/Build-Store.ps1",
        ):
            self.assertTrue((ROOT / path).is_file(), path)
        tools = (ROOT / "dotnet/src/LocalMorph.Core/Tools/ToolCatalog.cs").read_text()
        self.assertIn("winget install --id", tools)
        workflow = (ROOT / ".github/workflows/windows-store-publish.yml").read_text()
        self.assertIn("Internal Testing", workflow)
        self.assertIn("FLIGHT_UPLOAD_SHA256", workflow)

    def test_app_does_not_contain_a_direct_updater(self):
        sources = ROOT / "dotnet/src"
        for pattern in ("*.cs", "*.xaml", "*.plist"):
            for path in sources.rglob(pattern):
                if "obj" in path.parts or "bin" in path.parts:
                    continue
                text = path.read_text(encoding="utf-8-sig")
                for marker in (
                    "LOCALMORPH_APPCAST_URL", "AppcastReader", "SUFeedURL",
                    "DownloadUpdateCommand", "CopyUpdateCommand", "AutomaticUpdateChecks",
                ):
                    with self.subTest(path=path, marker=marker):
                        self.assertNotIn(marker, text)

    def test_tag_helpers_fail_before_mutation_and_route_only_to_stores(self):
        runners = [("bash", "scripts/create-release-tags.sh", "--windows", "--mac", "--push")]
        if shutil.which("pwsh"):
            runners.append(("pwsh", "scripts/create-release-tags.ps1", "-Windows", "-Mac", "-Push"))
        for runner, script, windows, mac, push in runners:
            for version, flags, succeeds in (
                ("v1.2.3", [windows, push], True),
                ("v1.2.3.0", [windows], True),
                ("v0.2.3", [windows], False),
                ("v1.2.3.1", [windows], False),
                ("v1.65536.3", [windows], False),
                ("invalid", [windows], False),
                ("v1.2.3", [], False),
            ):
                with self.subTest(runner=runner, version=version, flags=flags):
                    self.assert_tag_result(runner, script, version, flags, succeeds)
            if not (ROOT / ".github/workflows/mac-store-publish.yml").exists():
                for flags in ([mac], [windows, mac, push]):
                    with self.subTest(runner=runner, flags=flags):
                        self.assert_tag_result(runner, script, "v1.2.3", flags, False)

    def assert_tag_result(self, runner, script, version, flags, succeeds):
        with tempfile.TemporaryDirectory() as directory:
            temporary = Path(directory)
            git = temporary / "git"
            log = temporary / "calls"
            git.write_text(
                '#!/bin/sh\nprintf "%s\\n" "$*" >> "$TAG_TEST_LOG"\n'
                'case "$*" in\n'
                '  "rev-parse --is-inside-work-tree") echo true ;;\n'
                '  "rev-parse -q --verify "*) exit 1 ;;\n'
                '  "ls-remote "*) exit 0 ;;\n'
                'esac\n'
            )
            git.chmod(0o755)
            env = {**os.environ, "PATH": f"{directory}{os.pathsep}{os.environ['PATH']}",
                   "TAG_TEST_LOG": str(log)}
            command = [runner]
            if runner == "pwsh":
                command += ["-NoProfile", "-File"]
            result = subprocess.run(
                command + [str(ROOT / script), version, *flags],
                cwd=ROOT, env=env, capture_output=True, text=True,
            )
            self.assertEqual(result.returncode == 0, succeeds, result.stdout + result.stderr)
            calls = log.read_text().splitlines() if log.exists() else []
            mutations = [call for call in calls if call.startswith(("tag ", "push "))]
            if succeeds:
                self.assertIn(f"tag -a {version}-windows -m Release {version}-windows", calls)
            else:
                self.assertEqual([], mutations, calls)


if __name__ == "__main__":
    unittest.main()
