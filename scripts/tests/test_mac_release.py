"""Offline release guards; no Apple credentials, accounts or uploads are used."""

from datetime import datetime, timedelta, timezone
from argparse import Namespace
import hashlib
import json
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/mac"))
import store_release as release
import testflight


class MacReleaseTests(unittest.TestCase):
    def profile(self):
        return {"UUID": "12345678-1234-1234-1234-123456789012", "Platform": ["OSX"],
                "ExpirationDate": datetime.now(timezone.utc) + timedelta(days=2),
                "TeamIdentifier": ["TEAM123456"], "ApplicationIdentifierPrefix": ["TEAM123456"],
                "Entitlements": {"com.apple.application-identifier": "TEAM123456." + release.BUNDLE_ID,
                                 "com.apple.developer.team-identifier": "TEAM123456"}}

    def test_versions_reject_shell_syntax_four_components_and_invalid_builds(self):
        self.assertEqual(("1.2.3", "42"), release.versions("1.2.3", "42"))
        for version, build in [("1.2.3.0", "1"), ("1.2.3;echo", "1"), ("1.02.3", "1"),
                               ("1.2.3", "0"), ("1.2.3", "10000"), ("1.2.3", "2.1"),
                               ("1.2.1\u0662", "1"), ("1.2.3", "1\u0662")]:
            with self.subTest(version=version, build=build), self.assertRaises(ValueError):
                release.versions(version, build)

    def test_profile_requires_correct_app_team_platform_expiry_and_store_distribution(self):
        self.assertEqual("TEAM123456", release.validate_profile(self.profile()))
        for change in [{"Platform": ["iOS"]}, {"ProvisionedDevices": ["device"]},
                       {"ProvisionsAllDevices": True}, {"ExpirationDate": datetime(2000, 1, 1)},
                       {"Entitlements": {**self.profile()["Entitlements"], "get-task-allow": True}},
                       {"Entitlements": {**self.profile()["Entitlements"],
                                         "com.apple.application-identifier": "TEAM123456.wrong"}}]:
            with self.subTest(change=change), self.assertRaises(ValueError):
                release.validate_profile({**self.profile(), **change})

    def test_approval_requires_owner_no_bypass_and_exact_branch_tag_policy(self):
        environment = {"can_admins_bypass": False,
            "protection_rules": [{"type": "required_reviewers", "prevent_self_review": False,
                                 "reviewers": [{"type": "User", "reviewer": {"login": "jamesmontemagno"}}]}],
            "deployment_branch_policy": {"custom_branch_policies": True, "protected_branches": False}}
        policies = {"branch_policies": [{"name": "main", "type": "branch"}, {"name": "v*-mac", "type": "tag"}]}
        release.validate_approval(environment, policies)
        for change in [{"can_admins_bypass": True}, {"protection_rules": []},
                       {"deployment_branch_policy": {"protected_branches": True}}]:
            with self.subTest(change=change), self.assertRaises(ValueError):
                release.validate_approval({**environment, **change}, policies)
        with self.assertRaises(ValueError):
            release.validate_approval(environment, {"branch_policies": policies["branch_policies"] + [{"name": "*", "type": "branch"}]})

    def test_parent_and_helper_entitlements_cannot_gain_broad_exceptions(self):
        release.validate_entitlements(release.PARENT_ENTITLEMENTS)
        helper = {"com.apple.security.app-sandbox": True, "com.apple.security.inherit": True}
        release.validate_entitlements(helper, helper=True)
        for name in ("com.apple.security.cs.allow-jit", "com.apple.security.network.client",
                     "com.apple.security.cs.disable-library-validation", "get-task-allow"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                release.validate_entitlements({**release.PARENT_ENTITLEMENTS, name: True})
        with self.assertRaises(ValueError):
            release.validate_entitlements({**helper, "com.apple.security.files.user-selected.read-write": True}, helper=True)

    def test_store_profile_disabled_debug_flags_are_preserved_but_never_enabled(self):
        for key in release.DISABLED_DEBUG_ENTITLEMENTS:
            with self.subTest(key=key):
                parent = {**release.PARENT_ENTITLEMENTS,
                          "com.apple.application-identifier": "TEAM123456." + release.BUNDLE_ID,
                          "com.apple.developer.team-identifier": "TEAM123456", key: False}
                release.validate_entitlements(parent)
                for value in (True, 0, "false", None):
                    with self.subTest(value=value), self.assertRaisesRegex(ValueError, "Boolean false"):
                        release.validate_entitlements({**parent, key: value})
                helper = {"com.apple.security.app-sandbox": True, "com.apple.security.inherit": True, key: False}
                with self.assertRaisesRegex(ValueError, key):
                    release.validate_entitlements(helper, helper=True)

    def test_unknown_entitlement_error_names_the_key_without_disclosing_its_value(self):
        key = "com.apple.unexpected-capability"
        value = "do-not-log-entitlement-values"
        with self.assertRaises(ValueError) as failure:
            release.validate_entitlements({**release.PARENT_ENTITLEMENTS, key: value})
        self.assertIn(key, str(failure.exception))
        self.assertNotIn(value, str(failure.exception))

    def test_package_hash_source_version_and_single_artifact_are_authoritative(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            package = directory / "LocalMorph-1.2.3-4.pkg"
            package.write_bytes(b"signed fixture")
            manifest = {"source_commit": "a" * 40, "version": "1.2.3", "build": "4",
                        "package": package.name, "sha256": hashlib.sha256(package.read_bytes()).hexdigest()}
            (directory / "manifest.json").write_text(json.dumps(manifest))
            release.verify_manifest(directory, "a" * 40, "1.2.3", "4")
            for source, version, build in [("b" * 40, "1.2.3", "4"), ("a" * 40, "1.2.4", "4"),
                                           ("a" * 40, "1.2.3", "5")]:
                with self.assertRaises(ValueError):
                    release.verify_manifest(directory, source, version, build)
            package.write_bytes(b"tampered")
            with self.assertRaises(ValueError):
                release.verify_manifest(directory, "a" * 40, "1.2.3", "4")
            manifest["package"] = "../escape.pkg"
            (directory / "manifest.json").write_text(json.dumps(manifest))
            with self.assertRaises(ValueError):
                release.verify_manifest(directory, "a" * 40, "1.2.3", "4")

    def test_apple_credentials_are_never_sent_to_redirect_or_foreign_url(self):
        client = testflight.Client()
        with patch.object(client, "token", side_effect=AssertionError("Credentials accessed")):
            for url in ("https://evil.invalid/v1/apps", "http://api.appstoreconnect.apple.com/v1/apps",
                        "https://api.appstoreconnect.apple.com.evil.invalid/v1/apps"):
                with self.subTest(url=url), self.assertRaises(ValueError):
                    client.request(url)
            with self.assertRaises(ValueError):
                client.request("/v1/appStoreVersionSubmissions", method="POST", data={})

    def test_media_requires_both_distinct_raw_hashes_before_native_validation(self):
        with tempfile.TemporaryDirectory() as temporary:
            media = Path(temporary)
            (media / "bin").mkdir()
            (media / "source.txt").write_text(release.SOURCE_SHA256)
            for name in ("ffmpeg", "ffprobe"):
                (media / "bin" / name).write_bytes(name.encode())
            line = release.digest(media / "bin/ffmpeg") + "  bin/ffmpeg\n"
            for contents in ("", line, line + line, "0" * 64 + "  bin/ffmpeg\n"):
                with self.subTest(contents=contents):
                    (media / "binaries.sha256").write_text(contents)
                    with patch.object(release, "run", side_effect=AssertionError("Invalid media accepted")):
                        with self.assertRaises(ValueError):
                            release.validate_media(media)
            (media / "binaries.sha256").write_text("".join(
                release.digest(media / "bin" / name) + "  bin/" + name + "\n"
                for name in ("ffmpeg", "ffprobe")))
            (media / "configure-flags.txt").write_text("\n".join((
                "--disable-gpl", "--disable-nonfree", "--disable-autodetect",
                "--disable-network", "--enable-videotoolbox", "--enable-zlib")))
            with self.assertRaisesRegex(ValueError, "Unexpected media build configuration"):
                release.validate_media(media)
            (media / "configure-flags.txt").write_text("\n".join((
                "--disable-gpl", "--disable-nonfree", "--disable-autodetect", "--disable-network",
                "--enable-videotoolbox", "--enable-zlib", "--enable-libass", "--enable-libzimg")))
            with self.assertRaisesRegex(ValueError, "Missing bundled component license"):
                release.validate_media(media)
            (media / "licenses").mkdir()
            for name in release.DEPENDENCY_LICENSES:
                (media / "licenses" / name).write_text("license fixture")
            for name in ("ffmpeg", "ffprobe"):
                (media / "bin" / name).chmod(0o755)
            def native_run(command, *args):
                return b"arm64\n" if command == "lipo" else b"fixture:\n\t/usr/lib/libSystem.B.dylib\n"
            with patch.object(release, "run", side_effect=native_run):
                release.validate_media(media)
                if os.name == "posix":
                    (media / "bin/ffprobe").chmod(0o644)
                    with self.assertRaisesRegex(ValueError, "regular media executable"):
                        release.validate_media(media)

    def test_package_retains_corresponding_source_matching_symbols_and_complete_inventory(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            app = directory / "LocalMorph.app"
            native = app / "Contents/MacOS"
            native.mkdir(parents=True)
            (native / "LocalMorph").write_bytes(b"native app fixture")
            (app / "Contents/LocalMorph.pdb").write_bytes(b"managed symbols")
            (app / "Contents/Info.plist").write_bytes(plistlib.dumps({"CFBundleExecutable": "LocalMorph"}))
            media = directory / "payload"
            media.mkdir()
            (media / "sources").mkdir()
            dependency = media / "sources" / "libass-0.17.4.tar.xz"
            dependency.write_bytes(b"libass source fixture")
            (media / "source.txt").write_text(
                "Source: fixture\n" + release.digest(dependency) + "  libass-0.17.4.tar.xz  https://example.invalid/libass\n")
            archive = directory / "ffmpeg-9.0.2.tar.xz"
            archive.write_bytes(b"source archive fixture")
            symbol_root = directory / "build-symbols"
            dsym = symbol_root / "LocalMorph.app.dSYM"
            dsym.mkdir(parents=True)
            (dsym / "symbol").write_bytes(b"native symbols")
            output = directory / "output"
            github_output = directory / "github-output"
            installer = "Mac Installer Distribution: Fixture (TEAM123456)"
            metadata = {"source_commit": "a" * 40, "version": "1.2.3", "build": "4"}
            args = Namespace(app=app, media=media, version="1.2.3", build="4", source="a" * 40,
                             identity="app identity", keychain="fixture keychain", installer=installer,
                             symbols=symbol_root, output=output)
            commands = []
            def native_run(*command):
                commands.append(command)
                if command[0] == "security":
                    return plistlib.dumps(self.profile())
                if command[0] == "productbuild":
                    Path(command[-1]).write_bytes(b"signed installer fixture")
                    return b""
                if command[0] == "pkgutil":
                    return installer.encode()
                if command[0] == "dwarfdump":
                    return b"UUID: ABCDEF-1234 (arm64) fixture\n"
                raise AssertionError(command)
            with patch.object(release, "stage", return_value=metadata) as stage, \
                 patch.object(release, "entitlements", return_value={"com.apple.developer.team-identifier": "TEAM123456"}), \
                 patch.object(release, "run", side_effect=native_run), \
                 patch.object(release, "SOURCE_SHA256", release.digest(archive)), \
                 patch.dict(os.environ, {"GITHUB_OUTPUT": str(github_output)}):
                release.package(args)
            stage.assert_called_once_with(app, media, "1.2.3", "4", "a" * 40, "app identity", "fixture keychain")
            self.assertIn(("productbuild", "--sign", installer, "--keychain", "fixture keychain",
                           "--component", str(app), "/Applications", str(output / "LocalMorph-1.2.3-4.pkg")), commands)
            manifest = json.loads((output / "manifest.json").read_text())
            actual = {str(path.relative_to(output)): release.digest(path)
                      for path in output.rglob("*") if path.is_file() and path.name != "manifest.json"}
            self.assertEqual(actual, manifest["files"])
            self.assertIn("symbols/LocalMorph.app.dSYM/symbol", actual)
            self.assertIn("symbols/LocalMorph.pdb", actual)
            self.assertIn(archive.name, actual)
            self.assertIn("build-proof-media.sh", actual)
            self.assertIn("sources/libass-0.17.4.tar.xz", actual)
            self.assertNotIn("media-evidence/sources/libass-0.17.4.tar.xz", actual)
            dependency.write_bytes(b"tampered")
            with patch.object(release, "stage", return_value=metadata), \
                 patch.object(release, "entitlements", return_value={"com.apple.developer.team-identifier": "TEAM123456"}), \
                 patch.object(release, "run", side_effect=native_run), \
                 patch.object(release, "SOURCE_SHA256", release.digest(archive)), \
                 patch.dict(os.environ, {"GITHUB_OUTPUT": str(github_output)}):
                shutil.rmtree(output)
                with self.assertRaisesRegex(ValueError, "corresponding source"):
                    release.package(args)
            self.assertEqual("sha256=" + manifest["sha256"] + "\n", github_output.read_text())

    def test_cleanup_removes_only_its_installed_profile_and_temporary_material(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            signing = directory / "signing"
            signing.mkdir()
            profile = directory / "own.provisionprofile"
            profile.write_bytes(b"profile fixture")
            unrelated = directory / "other.provisionprofile"
            unrelated.write_bytes(b"preserve")
            (signing / "cleanup.json").write_text(json.dumps({"profile": str(profile)}))
            for name in ("certificate.p12", "profile.provisionprofile"):
                (signing / name).write_bytes(b"temporary fixture")
            release.cleanup(signing)
            self.assertFalse(signing.exists())
            self.assertFalse(profile.exists())
            self.assertEqual(b"preserve", unrelated.read_bytes())

    def test_internal_group_resolution_rejects_absent_external_and_ambiguous_groups(self):
        client = testflight.Client()
        app = {"id": "app", "attributes": {"bundleId": release.BUNDLE_ID}}
        internal = {"id": "group", "attributes": {"isInternalGroup": True, "name": "Internal"}}
        external = {"id": "external", "attributes": {"isInternalGroup": False, "name": "Internal"}}
        for groups, name, valid in [([internal], "", True), ([internal, external], "Internal", True),
                                    ([external], "", False), ([internal, internal], "", False),
                                    ([internal], "Wrong", False)]:
            with self.subTest(groups=groups, name=name):
                client.pages = lambda path, **query: iter([{"data": [app] if path == "/v1/apps" else groups}])
                if valid:
                    self.assertEqual((app, internal), client.resolve(name))
                else:
                    with self.assertRaises(ValueError):
                        client.resolve(name)

    def test_build_selection_checks_mac_platform_display_version_and_build(self):
        client = testflight.Client()
        build = {"id": "build", "attributes": {"version": "5", "processingState": "VALID"},
                 "relationships": {"preReleaseVersion": {"data": {"id": "release"}}}}
        for platform, version, count in [("MAC_OS", "1.0.0", 1), ("IOS", "1.0.0", 0), ("MAC_OS", "2.0.0", 0)]:
            client.pages = lambda *args, **kwargs: iter([{"data": [build], "included": [
                {"type": "preReleaseVersions", "id": "release", "attributes": {"version": version, "platform": platform}}]}])
            self.assertEqual(count, len(client.builds("app", "1.0.0", "5")))

    def test_failed_processing_never_assigns_or_submits_review(self):
        client = testflight.Client()
        client.builds = lambda *args: [{"id": "build", "attributes": {"processingState": "INVALID"}}]
        with patch.object(client, "request", side_effect=AssertionError("Unexpected mutation")):
            with self.assertRaisesRegex(ValueError, "rejected"):
                client.wait_and_assign({"id": "app"}, {"id": "group"}, "1.0.0", "1", 1)

    def test_processed_build_assignment_is_verified(self):
        client = testflight.Client()
        client.builds = lambda *args: [{"id": "build", "attributes": {"processingState": "VALID"}}]
        group = {"id": "group", "attributes": {"name": "Internal", "isInternalGroup": True}}
        with patch.object(client, "assigned", side_effect=[False, True]), patch.object(client, "request") as request:
            result = client.wait_and_assign({"id": "app"}, group, "1.0.0", "1", 1)
            self.assertEqual("build", result["build_id"])
            self.assertFalse(result["review_submitted"])
            request.assert_called_once_with("/v1/betaGroups/group/relationships/builds", method="POST",
                                           data={"data": [{"type": "builds", "id": "build"}]})
        with patch.object(client, "assigned", return_value=False), patch.object(client, "request"):
            with self.assertRaisesRegex(ValueError, "not confirmed"):
                client.wait_and_assign({"id": "app"}, group, "1.0.0", "1", 1)

    @unittest.skipUnless(shutil.which("openssl"), "openssl unavailable")
    def test_jwt_uses_real_es256_without_external_python_packages(self):
        with tempfile.TemporaryDirectory() as temporary:
            private = subprocess.check_output(["openssl", "ecparam", "-name", "prime256v1", "-genkey", "-noout"])
            with patch.dict(os.environ, {"APPLE_REFRACTORED_KEY_ID": "TESTKEY",
                                        "APPLE_REFRACTORED_ISSUER_ID": "test-issuer",
                                        "APPLE_REFRACTORED_P8_KEY": private.decode()}):
                token = testflight.Client().token()
            header, payload, signature = token.split(".")
            decode = lambda value: testflight.base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))
            self.assertEqual("ES256", json.loads(decode(header))["alg"])
            self.assertEqual("appstoreconnect-v1", json.loads(decode(payload))["aud"])
            raw = decode(signature)
            self.assertEqual(64, len(raw))
            def integer(value):
                value = value.lstrip(b"\0")
                if value[0] & 0x80:
                    value = b"\0" + value
                return b"\x02" + bytes([len(value)]) + value
            sequence = integer(raw[:32]) + integer(raw[32:])
            der = b"\x30" + bytes([len(sequence)]) + sequence
            directory = Path(temporary)
            (directory / "key.pem").write_bytes(private)
            public = subprocess.check_output(["openssl", "ec", "-in", str(directory / "key.pem"), "-pubout"],
                                             stderr=subprocess.DEVNULL)
            (directory / "public.pem").write_bytes(public)
            (directory / "signature").write_bytes(der)
            result = subprocess.run(["openssl", "dgst", "-sha256", "-verify", str(directory / "public.pem"),
                                     "-signature", str(directory / "signature")],
                                    input=(header + "." + payload).encode(), capture_output=True)
            self.assertEqual(0, result.returncode, result.stderr)


if __name__ == "__main__":
    unittest.main()
