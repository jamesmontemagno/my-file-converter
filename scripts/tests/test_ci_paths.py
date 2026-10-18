"""Check directory routing without starting builds or contacting GitHub."""

from pathlib import Path
import ast
import fnmatch
import re
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from ci_paths import affected, changed_paths


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
        self.assertEqual({"distribution", "windows"}, self.selected("dotnet/packaging/windows/StoreManifest.xml"))

    def test_shared_code_and_build_configuration_keep_cross_platform_coverage(self):
        for path in ("dotnet/src/LocalMorph.Core/Jobs/ConversionRunner.cs",
                     "dotnet/src/LocalMorph.App/Services/AppSettings.cs",
                     "dotnet/src/LocalMorph.App/LocalMorph.App.csproj",
                     "dotnet/src/LocalMorph.App/Resources/Styles/Styles.xaml"):
            self.assertEqual({"distribution", "tests", "windows", "mac"}, self.selected(path))
        for path in ("dotnet/Directory.Build.props", "global.json", "dotnet/LocalMorph.slnx",
                     "scripts/ci_paths.py", ".github/workflows/bridge-ci.yml"):
            self.assertEqual({"distribution", "tests", "windows", "mac"}, self.selected(path))

    def test_helpers_and_tests_do_not_rebuild_unrelated_apps(self):
        self.assertEqual({"distribution"}, self.selected("scripts/tests/test_mac_release.py"))
        self.assertEqual({"distribution"}, self.selected("scripts/create-release-tags.ps1"))
        self.assertEqual({"tests"}, self.selected("dotnet/tests/LocalMorph.Core.Tests/QueueTests.cs"))
        self.assertEqual({"tests"}, self.selected(".github/workflows/bridge-release.yml"))

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
        self.assertEqual(1, workflow.count("bash scripts/mac/build-proof-media.sh"))
        self.assertIn("name: mac-store-package", workflow)
        self.assertIn("PACKAGE_SHA256:", workflow)
        self.assertIn("needs: [validate, package]", workflow)


if __name__ == "__main__":
    unittest.main()
