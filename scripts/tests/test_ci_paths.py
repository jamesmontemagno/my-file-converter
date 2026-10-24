"""Check directory routing without starting builds or contacting GitHub."""

from pathlib import Path
import ast
import fnmatch
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from ci_paths import affected, changed_paths, main


class CiPathTests(unittest.TestCase):
    def selected(self, *paths):
        return {name for name, value in affected(paths).items() if value}

    def test_website_docs_and_mobile_changes_do_not_build_desktop(self):
        for path in ("src/App.tsx", "public/launch.mp4", "docs/release-setup.md", "README.md",
                     "dotnet/CHANGELOG.md", ".github/workflows/ci.yml", ".github/workflows/deploy.yml",
                     "dotnet/src/LocalMorph.App/Platforms/iOS/AppDelegate.cs",
                     "dotnet/src/LocalMorph.App/Platforms/Android/MainActivity.cs"):
            with self.subTest(path=path):
                self.assertEqual(set(), self.selected(path))

    def test_platform_changes_only_build_the_affected_app(self):
        self.assertEqual({"distribution", "mac"}, self.selected("dotnet/src/LocalMorph.App/Platforms/MacCatalyst/Info.plist"))
        self.assertEqual({"distribution", "mac"}, self.selected("dotnet/src/LocalMorph.App/Platforms/iOS/Resources/PrivacyInfo.xcprivacy"))
        self.assertEqual({"distribution", "windows"}, self.selected("dotnet/src/LocalMorph.App/Platforms/Windows/App.xaml.cs"))
        self.assertEqual({"distribution", "windows"}, self.selected("dotnet/src/LocalMorph.Shell/Commands.cpp"))
        self.assertEqual({"distribution", "mac"}, self.selected("scripts/mac/store_release.py"))
        self.assertEqual({"distribution", "mac"}, self.selected(".github/actions/mac-media/action.yml"))
        self.assertEqual({"distribution", "mac"}, self.selected(".github/workflows/mac-proof.yml"))
        self.assertEqual({"distribution", "windows"}, self.selected("dotnet/packaging/windows/StoreManifest.xml"))

    def test_app_only_changes_do_not_run_bridge_or_core_tests(self):
        for path in ("dotnet/src/LocalMorph.App/Services/AppSettings.cs",
                     "dotnet/src/LocalMorph.App/LocalMorph.App.csproj",
                     "dotnet/src/LocalMorph.App/Resources/Styles/Styles.xaml",
                     "dotnet/src/LocalMorph.App/MainPage.xaml",
                     "dotnet/src/LocalMorph.App/ViewModels/MainViewModel.About.cs"):
            with self.subTest(path=path):
                self.assertEqual({"distribution", "windows", "mac"}, self.selected(path))

    def test_core_and_shared_build_changes_keep_all_dependent_coverage(self):
        for path in ("dotnet/src/LocalMorph.Core/Jobs/ConversionRunner.cs",
                     "dotnet/src/LocalMorph.Core/Support/FeedbackIssue.cs",
                     "dotnet/src/LocalMorph.Core/LocalMorph.Core.csproj",
                     "dotnet/Directory.Build.props", "global.json", "dotnet/LocalMorph.slnx",
                     "scripts/ci_paths.py", ".github/workflows/bridge-ci.yml"):
            with self.subTest(path=path):
                self.assertEqual({"distribution", "bridge", "core", "windows", "mac"}, self.selected(path))

    def test_shared_windows_cache_setup_runs_all_consuming_coverage(self):
        self.assertEqual({"distribution", "bridge", "core", "windows", "mac"},
                         self.selected("scripts/windows/Initialize-NuGetCache.ps1"))

    def test_windows_package_caches_follow_effective_location_before_dotnet_setup(self):
        root = Path(__file__).resolve().parents[2]
        bridge = (root / ".github/workflows/bridge-ci.yml").read_text()
        store = (root / ".github/workflows/windows-store-publish.yml").read_text()
        jobs = [
            bridge.split("\n  test:", 1)[1].split("\n  desktop-app:", 1)[0],
            bridge.split("\n  desktop-app:", 1)[1].split("\n  desktop-app-mac:", 1)[0],
            store.split("\n  package:", 1)[1].split("\n  diagnose:", 1)[0],
        ]
        for job in jobs:
            self.assertLess(job.index("Initialize-NuGetCache.ps1"), job.index("actions/setup-dotnet@"))
            self.assertIn("if: runner.os == 'Windows' && runner.environment == 'github-hosted'", job)
            self.assertIn("RUNNER_ENVIRONMENT: ${{ runner.environment }}", job)
            self.assertIn("path: ${{ env.NUGET_PACKAGES || '~/.nuget/packages' }}", job)
            self.assertIn("key: nuget-v2-${{ runner.os }}-${{ runner.arch }}", job)
            self.assertNotIn("nuget-v1-", job)
            self.assertNotIn("path: ${{ env.NUGET_HTTP_CACHE_PATH", job)
        self.assertNotIn("Initialize-NuGetCache.ps1", store.split("\n  diagnose:", 1)[1])

    @unittest.skipUnless(shutil.which("pwsh"), "PowerShell is required to exercise cache setup")
    def test_cache_setup_guards_and_preserves_explicit_paths(self):
        script = Path(__file__).resolve().parents[1] / "windows/Initialize-NuGetCache.ps1"
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            output = base / "github-env"
            environment = dict(os.environ, RUNNER_ENVIRONMENT="self-hosted",
                               RUNNER_TEMP=str(base), GITHUB_ENV=str(output))
            for name in ("NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH"):
                environment.pop(name, None)

            def invoke():
                return subprocess.run(["pwsh", "-NoProfile", "-File", str(script)],
                                      env=environment, capture_output=True, text=True)

            self.assertEqual(0, invoke().returncode)
            self.assertFalse(output.exists())
            environment["RUNNER_ENVIRONMENT"] = "github-hosted"
            if os.name != "nt":
                self.assertEqual(0, invoke().returncode)
                self.assertFalse(output.exists())
                return
            result = invoke()
            self.assertEqual(0, result.returncode, result.stderr)
            paths = dict(line.split("=", 1) for line in output.read_text(encoding="utf-8-sig").splitlines())
            self.assertEqual(str(base / "nuget/packages"), paths["NUGET_PACKAGES"])
            self.assertEqual(str(base / "nuget/http-cache"), paths["NUGET_HTTP_CACHE_PATH"])
            self.assertTrue(all(Path(path).is_dir() for path in paths.values()))
            output.unlink()
            environment["NUGET_PACKAGES"] = str(base / "explicit-packages")
            self.assertEqual(0, invoke().returncode)
            self.assertNotIn("NUGET_PACKAGES=", output.read_text(encoding="utf-8-sig"))
            self.assertIn("NUGET_HTTP_CACHE_PATH=", output.read_text(encoding="utf-8-sig"))
            output.unlink()
            environment["NUGET_HTTP_CACHE_PATH"] = str(base / "explicit-http")
            self.assertEqual(0, invoke().returncode)
            self.assertFalse(output.exists())
            environment.pop("RUNNER_TEMP")
            self.assertNotEqual(0, invoke().returncode)

    def test_bridge_changes_do_not_rebuild_apps_or_run_core_tests(self):
        for path in ("dotnet/src/LocalMorph.Bridge/BridgeApplication.cs",
                     "dotnet/src/LocalMorph.Bridge/LocalMorph.Bridge.csproj"):
            with self.subTest(path=path):
                self.assertEqual({"distribution", "bridge"}, self.selected(path))

    def test_only_the_affected_test_project_runs(self):
        self.assertEqual({"core"}, self.selected("dotnet/tests/LocalMorph.Core.Tests/QueueTests.cs"))
        self.assertEqual({"bridge"}, self.selected("dotnet/tests/LocalMorph.Bridge.Tests/BridgeTests.cs"))
        self.assertEqual({"bridge", "core"}, self.selected("dotnet/tests/shared.runsettings"))

    def test_multiple_components_accumulate_their_required_checks(self):
        self.assertEqual({"distribution", "bridge", "core", "windows", "mac"}, self.selected(
            "dotnet/src/LocalMorph.App/MainPage.xaml",
            "dotnet/tests/LocalMorph.Bridge.Tests/BridgeTests.cs",
            "dotnet/tests/LocalMorph.Core.Tests/FeedbackIssueTests.cs"))
        self.assertEqual({"distribution", "bridge", "windows"}, self.selected(
            "dotnet/src/LocalMorph.App/Platforms/Windows/OldFile.cs",
            "dotnet/src/LocalMorph.Bridge/MovedFile.cs"))

    def test_action_outputs_match_the_json_summary(self):
        with tempfile.TemporaryDirectory() as directory:
            event = Path(directory) / "event.json"
            output = Path(directory) / "output"
            event.write_text("{}")
            with patch.dict("os.environ", {"GITHUB_EVENT_NAME": "pull_request",
                                          "GITHUB_EVENT_PATH": str(event),
                                          "GITHUB_OUTPUT": str(output)}), \
                    patch("ci_paths.changed_paths", return_value=["dotnet/src/LocalMorph.App/MainPage.xaml"]), \
                    patch("builtins.print") as summary:
                main()
            outputs = dict(line.split("=", 1) for line in output.read_text().splitlines())
        self.assertEqual({"distribution": "true", "bridge": "false", "core": "false",
                          "windows": "true", "mac": "true"}, outputs)
        self.assertEqual({name: value == "true" for name, value in outputs.items()},
                         json.loads(summary.call_args.args[0]))

    def test_workflow_gates_the_matrix_and_each_test_or_pack_step(self):
        root = Path(__file__).resolve().parents[2]
        workflow = (root / ".github/workflows/bridge-ci.yml").read_text()
        self.assertNotIn("needs.changes.outputs.tests", workflow)
        for component in ("bridge", "core"):
            self.assertIn(component + ": ${{ steps.paths.outputs." + component + " }}", workflow)
        self.assertIn("if: needs.changes.outputs.bridge == 'true' || needs.changes.outputs.core == 'true'", workflow)
        for name, component, command in (
                ("Test Bridge", "bridge", "dotnet test dotnet/tests/LocalMorph.Bridge.Tests/LocalMorph.Bridge.Tests.csproj"),
                ("Test Core", "core", "dotnet test dotnet/tests/LocalMorph.Core.Tests/LocalMorph.Core.Tests.csproj"),
                ("Pack Bridge", "bridge", "dotnet pack dotnet/src/LocalMorph.Bridge/LocalMorph.Bridge.csproj")):
            with self.subTest(step=name):
                self.assertIn(f"      - name: {name}\n"
                              f"        if: needs.changes.outputs.{component} == 'true'\n"
                              f"        run: {command} --configuration Release", workflow)

    def test_helpers_and_tests_do_not_rebuild_unrelated_apps(self):
        self.assertEqual({"distribution"}, self.selected("scripts/tests/test_mac_release.py"))
        self.assertEqual({"distribution"}, self.selected("scripts/create-release-tags.ps1"))
        self.assertEqual({"bridge"}, self.selected(".github/workflows/bridge-release.yml"))

    def test_windows_isolates_sdk_but_keeps_pinned_install_and_full_build_coverage(self):
        root = Path(__file__).resolve().parents[2]
        bridge = (root / ".github/workflows/bridge-ci.yml").read_text()
        desktop = bridge.split("\n  desktop-app:", 1)[1].split("\n  desktop-app-mac:", 1)[0]
        store = (root / ".github/workflows/windows-store-publish.yml").read_text()
        package = store.split("\n  package:", 1)[1].split("\n  diagnose:", 1)[0]
        for job in (desktop, package):
            self.assertIn(r"DOTNET_INSTALL_DIR=$env:RUNNER_TEMP\localmorph-dotnet", job)
            self.assertLess(job.index("DOTNET_INSTALL_DIR="), job.index("actions/setup-dotnet@"))
            self.assertNotIn("${{ runner.temp }}", job)
            self.assertIn("(Get-Command dotnet | Select-Object -First 1).Source", job)
            self.assertIn("if ($resolved -ne $expected) { throw", job)
            self.assertIn(r"metadata\workloads\10.0.400\installertype\msi", job)
            self.assertIn("if (Test-Path -LiteralPath $msi) { throw", job)
            self.assertIn("dotnet workload install maui-windows --version 10.0.401.1", job)
            self.assertIn("$workload -ne '10.0.401.1'", job)
            self.assertNotIn("--skip-manifest-update", job)
            self.assertNotIn("Remove-Item", job)
        self.assertIn("--configuration Release -p:WindowsOnly=true", desktop)
        for architecture in ("x64", "ARM64"):
            self.assertIn("Build-Shell.ps1 -Architecture " + architecture, desktop)
        self.assertIn("Test-StoreScripts.ps1", desktop)
        self.assertIn("Build-Store.ps1", package)
        self.assertIn("environment: microsoft-store", store)
        self.assertIn("needs: [package, flight]", store)

    def test_reintroducing_direct_distribution_still_runs_guards(self):
        for path in ("Casks/localmorph.rb", "public/appcast.xml", "dotnet/packaging/winget/installer.yaml",
                     ".github/workflows/mac-release.yml", ".github/workflows/windows-release.yml",
                     ".github/workflows/winget-submit.yml"):
            self.assertEqual({"distribution"}, self.selected(path))

    def test_pull_request_uses_merge_base_and_preserves_both_renamed_paths(self):
        event = {"pull_request": {"base": {"sha": "a" * 40}, "head": {"sha": "b" * 40}}}
        with patch("ci_paths.subprocess.check_output", return_value=b"old/path\0new/path\0") as git:
            self.assertEqual(["old/path", "new/path"], changed_paths("pull_request", event))
        git.assert_called_once_with(["git", "diff", "--no-renames", "--name-only", "-z",
                                     "a" * 40 + "..." + "b" * 40])

    def test_push_uses_the_whole_push_range_and_new_branch_uses_all_files(self):
        for base, command in [("a" * 40, ["git", "diff", "--no-renames", "--name-only", "-z",
                                         "a" * 40 + ".." + "b" * 40]),
                              ("0" * 40, ["git", "ls-tree", "-r", "--name-only", "-z", "b" * 40])]:
            with patch("ci_paths.subprocess.check_output", return_value=b"file\0") as git:
                self.assertEqual(["file"], changed_paths("push", {"before": base, "after": "b" * 40}))
            git.assert_called_once_with(command)

    def test_invalid_diff_inputs_fail_instead_of_silently_skipping_checks(self):
        with self.assertRaises(ValueError):
            changed_paths("push", {"before": "--bad", "after": "b" * 40})
        with self.assertRaises(ValueError):
            changed_paths("workflow_dispatch", {})

    def test_website_and_pages_only_trigger_for_their_inputs(self):
        root = Path(__file__).resolve().parents[2]
        for workflow in ("ci.yml", "deploy.yml"):
            text = (root / ".github/workflows" / workflow).read_text()
            groups = re.findall(r"^    paths:\n((?:      - .+\n)+)", text, re.MULTILINE)
            self.assertEqual(2 if workflow == "ci.yml" else 1, len(groups))
            for group in groups:
                patterns = [ast.literal_eval(line.strip()[2:]) for line in group.splitlines()]
                for path in ("src/App.tsx", "public/mac/poster.png", "index.html", "package-lock.json",
                             "tsconfig.json", "vite.config.ts", ".github/workflows/" + workflow):
                    self.assertTrue(any(fnmatch.fnmatchcase(path, pattern) for pattern in patterns), path)
                for path in ("dotnet/src/LocalMorph.Core/Engine.cs", "scripts/mac/store_release.py",
                             "docs/release-setup.md", "README.md", "store/mac-app-store/listing.json"):
                    self.assertFalse(any(fnmatch.fnmatchcase(path, pattern) for pattern in patterns), path)

    def test_release_keeps_only_the_necessary_package_artifact_handoff(self):
        root = Path(__file__).resolve().parents[2]
        workflow = (root / ".github/workflows/mac-store-publish.yml").read_text()
        self.assertNotIn("\n  media:\n", workflow)
        self.assertNotIn("mac-store-beta-media", workflow)
        self.assertNotIn("unittest discover", workflow)
        self.assertEqual(1, workflow.count("uses: ./.github/actions/mac-media"))
        self.assertIn("name: mac-store-package", workflow)
        self.assertIn("PACKAGE_SHA256:", workflow)
        self.assertIn("needs: [validate, package]", workflow)


if __name__ == "__main__":
    unittest.main()
