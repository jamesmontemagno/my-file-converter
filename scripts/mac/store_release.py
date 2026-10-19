#!/usr/bin/env python3
"""Validate and assemble the Mac Store beta without using host media tools."""

import argparse
import base64
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.request


ROOT = Path(__file__).resolve().parents[2]
BUNDLE_ID = "com.refractored.localmorph"
SOURCE_SHA256 = "8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e"
DEPENDENCY_LICENSES = ("FreeType-FTL.txt", "FriBidi-COPYING.txt", "HarfBuzz-COPYING.txt",
                       "libass-COPYING.txt", "zimg-COPYING.txt")
PARENT_ENTITLEMENTS = {
    "com.apple.security.app-sandbox": True,
    "com.apple.security.files.user-selected.read-write": True,
    "com.apple.security.files.bookmarks.app-scope": True,
}
IDENTITY_ENTITLEMENTS = {
    "com.apple.application-identifier", "application-identifier",
    "com.apple.developer.team-identifier", "keychain-access-groups",
}
DISABLED_DEBUG_ENTITLEMENTS = {"get-task-allow", "com.apple.security.get-task-allow"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def run(*command):
    result = subprocess.run(command, capture_output=True)
    require(result.returncode == 0, f"{command[0]} failed: {result.stderr.decode(errors='replace').strip()}")
    return result.stdout


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def versions(version, build):
    require(re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", version),
            "Use a three-component display version, for example 1.0.0.")
    require(re.fullmatch(r"[1-9][0-9]{0,3}", build), "Build number must be an integer from 1 to 9999.")
    return version, build


def validate_approval(environment, policies):
    require(environment.get("can_admins_bypass") is False, "Disable Mac environment administrator bypass.")
    rules = environment.get("protection_rules", [])
    required = [rule for rule in rules if rule.get("type") == "required_reviewers"]
    reviewers = required[0].get("reviewers", []) if len(required) == 1 else []
    require(len(reviewers) == 1 and reviewers[0].get("type") == "User"
            and reviewers[0].get("reviewer", {}).get("login") == "jamesmontemagno",
            "Mac releases require the owner's environment approval.")
    branch_policy = environment.get("deployment_branch_policy") or {}
    require(branch_policy.get("custom_branch_policies") is True
            and branch_policy.get("protected_branches") is False, "Use explicit Mac deployment branch/tag rules.")
    actual = {(rule["name"], rule["type"]) for rule in policies.get("branch_policies", [])}
    require(actual == {("main", "branch"), ("v*-mac", "tag")}, "Allow only main and v*-mac deployments.")
    require(required[0].get("prevent_self_review") is False,
            "The configured owner-approved policy allows self-review, matching Windows.")


def approval():
    repository = os.environ["GITHUB_REPOSITORY"]
    require(repository == "jamesmontemagno/my-file-converter", "Unexpected release repository.")
    headers = {"Authorization": f"Bearer {os.environ['GH_TOKEN']}", "Accept": "application/vnd.github+json"}
    prefix = f"https://api.github.com/repos/{repository}/environments/mac-app-store"
    def get(url):
        with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30) as response:
            return json.load(response)
    validate_approval(get(prefix), get(prefix + "/deployment-branch-policies?per_page=100"))
    print("Mac approval, administrator-bypass and branch/tag policies verified.")


def validate_profile(profile, now=None):
    now = now or datetime.now(timezone.utc)
    expiry = profile.get("ExpirationDate")
    require(isinstance(expiry, datetime) and expiry.replace(tzinfo=timezone.utc) > now,
            "The Mac provisioning profile has expired or lacks an expiry.")
    require(re.fullmatch(r"[A-Fa-f0-9-]{36}", profile.get("UUID", "")), "Invalid provisioning UUID.")
    require(set(profile.get("Platform", [])) & {"OSX", "macOS"}, "A Mac App Store provisioning profile is required.")
    require(not profile.get("ProvisionedDevices") and not profile.get("ProvisionsAllDevices"),
            "Development, ad-hoc and enterprise profiles are not Store profiles.")
    entitlements = profile.get("Entitlements", {})
    require(not entitlements.get("get-task-allow") and not entitlements.get("com.apple.security.get-task-allow"),
            "The Store profile must not allow debugging.")
    teams = profile.get("TeamIdentifier", [])
    prefixes = profile.get("ApplicationIdentifierPrefix", [])
    require(len(teams) == 1 and len(prefixes) == 1, "Expected one provisioning team and app prefix.")
    identifier = entitlements.get("com.apple.application-identifier", entitlements.get("application-identifier"))
    require(identifier == prefixes[0] + "." + BUNDLE_ID, "Provisioning profile does not match LocalMorph's bundle ID.")
    require(entitlements.get("com.apple.developer.team-identifier") == teams[0], "Profile team identifiers disagree.")
    return teams[0]


def private_write(path, data):
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "wb") as stream:
        stream.write(data)


def signing(directory):
    directory.mkdir(parents=True, exist_ok=False)
    os.chmod(directory, 0o700)
    password = os.environ["APPLE_MACCATALYST_APPSTORE_CERTIFICATE_PASSWORD"]
    p12 = directory / "certificate.p12"
    profile_path = directory / "profile.provisionprofile"
    private_write(p12, base64.b64decode("".join(os.environ["APPLE_MACCATALYST_APPSTORE_CERTIFICATE_P12"].split()), validate=True))
    private_write(profile_path, base64.b64decode("".join(os.environ["APPLE_MACCATALYST_APPSTORE_PROFILE"].split()), validate=True))
    profile = plistlib.loads(run("security", "cms", "-D", "-i", str(profile_path)))
    team = validate_profile(profile)
    keychain = directory / "release.keychain-db"
    keychain_password = os.urandom(32).hex()
    print(f"::add-mask::{keychain_password}")
    # Do not include credential-bearing arguments in exceptions or logs.
    for command in (
        ["security", "create-keychain", "-p", keychain_password, str(keychain)],
        ["security", "set-keychain-settings", "-lut", "21600", str(keychain)],
        ["security", "unlock-keychain", "-p", keychain_password, str(keychain)],
        ["security", "import", str(p12), "-k", str(keychain), "-P", password,
         "-T", "/usr/bin/codesign", "-T", "/usr/bin/productbuild", "-T", "/usr/bin/productsign"],
        ["security", "set-key-partition-list", "-S", "apple-tool:,apple:,codesign:", "-s",
         "-k", keychain_password, str(keychain)],
    ):
        result = subprocess.run(command, capture_output=True)
        require(result.returncode == 0, "Could not prepare the temporary signing keychain. Check the P12 and password.")
    app_ids = re.findall(r'([A-Fa-f0-9]{40}) "((?:Apple Distribution|3rd Party Mac Developer Application): [^"]+)"',
                         run("security", "find-identity", "-v", "-p", "codesigning", str(keychain)).decode())
    pkg_ids = re.findall(r'([A-Fa-f0-9]{40}) "((?:Mac Installer Distribution|3rd Party Mac Developer Installer): [^"]+)"',
                         run("security", "find-identity", "-v", "-p", "basic", str(keychain)).decode())
    require(len(app_ids) == len(pkg_ids) == 1, "P12 needs exactly one application and one installer distribution private-key identity.")
    require(all(name.endswith(f"({team})") for _, name in app_ids + pkg_ids), "Certificate identities do not match the profile team.")
    hashes = {hashlib.sha1(certificate).hexdigest().upper() for certificate in profile.get("DeveloperCertificates", [])}
    require(app_ids[0][0].upper() in hashes, "The profile does not authorize this application signing certificate.")
    installed = Path.home() / "Library/MobileDevice/Provisioning Profiles" / (profile["UUID"] + ".provisionprofile")
    require(not installed.exists(), "Refusing to overwrite an existing provisioning profile.")
    installed.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(profile_path, installed)
    state = {"keychain": str(keychain), "profile": str(installed)}
    (directory / "cleanup.json").write_text(json.dumps(state))
    outputs = {"app_key": app_ids[0][0], "pkg_key": pkg_ids[0][1], "profile_uuid": profile["UUID"],
               "team": team, "keychain": str(keychain)}
    with open(os.environ["GITHUB_OUTPUT"], "a") as output:
        for name, value in outputs.items():
            output.write(f"{name}={value}\n")
    p12.unlink()


def cleanup(directory):
    state_file = directory / "cleanup.json"
    if state_file.exists():
        state = json.loads(state_file.read_text())
        Path(state["profile"]).unlink(missing_ok=True)
    keychain = directory / "release.keychain-db"
    if keychain.exists():
        run("security", "delete-keychain", str(keychain))
    for name in ("certificate.p12", "profile.provisionprofile", "cleanup.json"):
        (directory / name).unlink(missing_ok=True)
    if directory.exists():
        directory.rmdir()


def validate_entitlements(entitlements, helper=False):
    expected = {"com.apple.security.app-sandbox": True, "com.apple.security.inherit": True} if helper else PARENT_ENTITLEMENTS
    require(all(entitlements.get(key) is value for key, value in expected.items()), "Required sandbox entitlements are missing.")
    allowed = set(expected)
    if not helper:
        allowed |= IDENTITY_ENTITLEMENTS | DISABLED_DEBUG_ENTITLEMENTS
        require(all(key not in entitlements or entitlements[key] is False for key in DISABLED_DEBUG_ENTITLEMENTS),
                "Store distribution debugging entitlements must be Boolean false.")
    unexpected = set(entitlements) - allowed
    require(not unexpected, "Unexpected sandbox/runtime entitlements; no broad exceptions are permitted: "
            + ", ".join(sorted(unexpected)))


def entitlements(path):
    return plistlib.loads(run("codesign", "-d", "--entitlements", ":-", str(path)))


def validate_media(media):
    require((media / "source.txt").read_text().find(SOURCE_SHA256) >= 0, "Unexpected FFmpeg source pin.")
    checked = set()
    for line in (media / "binaries.sha256").read_text().splitlines():
        expected, original = line.split(maxsplit=1)
        name = Path(original.lstrip("*")).name
        require(name in {"ffmpeg", "ffprobe"} and digest(media / "bin" / name) == expected, "Raw media hash mismatch.")
        require(name not in checked, "Duplicate raw media hash entry.")
        checked.add(name)
    require(checked == {"ffmpeg", "ffprobe"}, "Both raw media hashes are required.")
    flags = (media / "configure-flags.txt").read_text().splitlines()
    require(all(flag in flags for flag in ("--disable-gpl", "--disable-nonfree", "--disable-autodetect",
                                         "--disable-network", "--enable-videotoolbox", "--enable-zlib",
                                         "--enable-libass", "--enable-libzimg")),
            "Unexpected media build configuration.")
    for name in DEPENDENCY_LICENSES:
        require((media / "licenses" / name).is_file(), f"Missing bundled component license {name}.")
    for name in ("ffmpeg", "ffprobe"):
        tool = media / "bin" / name
        require(tool.is_file() and not tool.is_symlink() and os.access(tool, os.X_OK), "Missing regular media executable.")
        require(run("lipo", "-archs", str(tool)).decode().strip() == "arm64", "Media tools must be arm64 only.")
        dependencies = run("otool", "-L", str(tool)).decode().splitlines()[1:]
        require(dependencies and all(line.strip().startswith(("/System/Library/", "/usr/lib/"))
                                     for line in dependencies if line.strip()), "Media has a non-system dependency.")


def stage(app, media, version, build, source, identity, keychain=None):
    versions(version, build)
    validate_media(media)
    info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
    require(info["CFBundleIdentifier"] == BUNDLE_ID, "Not a production-identity Mac Store build.")
    require(info["CFBundleShortVersionString"] == version and info["CFBundleVersion"] == build, "App version mismatch.")
    privacy = plistlib.loads((app / "Contents/Resources/PrivacyInfo.xcprivacy").read_bytes())
    reasons = {item["NSPrivacyAccessedAPIType"]: item["NSPrivacyAccessedAPITypeReasons"]
               for item in privacy["NSPrivacyAccessedAPITypes"]}
    require("CA92.1" in reasons.get("NSPrivacyAccessedAPICategoryUserDefaults", []),
            "The SDK privacy manifest must declare app-local preferences.")
    run(sys.executable, str(ROOT / "scripts/mac/validate_app.py"), str(app))
    parent = entitlements(app)
    validate_entitlements(parent)
    native = app / "Contents/MacOS"
    for name in ("ffmpeg", "ffprobe"):
        destination = native / name
        require(not destination.exists(), "Refusing to overwrite a pre-existing bundled media tool.")
        shutil.copy2(media / "bin" / name, destination)
        command = ["codesign", "--force", "--sign", identity, "--options", "runtime",
                   "--entitlements", str(ROOT / "dotnet/src/LocalMorph.App/Platforms/MacCatalyst/MediaTool.entitlements")]
        if keychain:
            command += ["--keychain", keychain]
        run(*command, str(destination))
        validate_entitlements(entitlements(destination), helper=True)
    notices = app / "Contents/Resources/FFmpeg"
    notices.mkdir(parents=True, exist_ok=False)
    for name in ("LICENSE.md", "COPYING.LGPLv2.1", "configure-flags.txt", "source.txt"):
        shutil.copyfile(media / name, notices / name)
    shutil.copytree(media / "licenses", notices / "licenses")
    shutil.copyfile(ROOT / "dotnet/packaging/mac/FFmpeg-beta-NOTICE.txt", notices / "FFmpeg-proof-NOTICE.txt")
    shutil.copyfile(ROOT / "dotnet/packaging/mac/ThirdPartyNotices.txt", app / "Contents/Resources/ThirdPartyNotices.txt")
    metadata = {"schema": 1, "channel": "mac-testflight-beta", "bundle_id": BUNDLE_ID,
                "version": version, "build": build, "source_commit": source,
                "ffmpeg_source_sha256": SOURCE_SHA256,
                "tools": {name: digest(native / name) for name in ("ffmpeg", "ffprobe")},
                "components": ["FFmpeg 9.0.2", "libass 0.17.4", "FreeType 2.13.3", "FriBidi 1.0.16",
                               "HarfBuzz 11.2.1", "zimg 3.0.6"],
                "limitations": ["No external engines; macOS built-in converters replace ImageMagick/LibreOffice/Pandoc/Ghostscript"]}
    (app / "Contents/Resources/localmorph-build.json").write_text(json.dumps(metadata, indent=2) + "\n")
    # The SDK already signed the runtime libraries and merged profile entitlements.
    # Re-sign only the enclosing app after adding the separately signed media tools.
    with tempfile.TemporaryDirectory() as temporary:
        path = Path(temporary) / "parent.plist"
        path.write_bytes(plistlib.dumps(parent))
        command = ["codesign", "--force", "--sign", identity, "--options", "runtime", "--entitlements", str(path)]
        if keychain:
            command += ["--keychain", keychain]
        run(*command, str(app))
    run("codesign", "--verify", "--deep", "--strict", str(app))
    return metadata


def verify_manifest(directory, source, version, build):
    versions(version, build)
    manifest = json.loads((directory / "manifest.json").read_text())
    require((manifest.get("source_commit"), manifest.get("version"), manifest.get("build"))
            == (source, version, build), "Package provenance does not match the requested build.")
    require(re.fullmatch(r"LocalMorph-[0-9.]+-[0-9]+\.pkg", manifest.get("package", "")), "Invalid package filename.")
    package = directory / manifest["package"]
    require(package.is_file() and not package.is_symlink() and digest(package) == manifest["sha256"], "Package hash mismatch.")
    require(len(list(directory.glob("*.pkg"))) == 1, "Expected exactly one upload package.")
    return package, manifest


def package(args):
    require(not args.output.exists(), "Refusing to overwrite a package artifact.")
    args.output.mkdir(parents=True)
    metadata = stage(args.app, args.media, args.version, args.build, args.source, args.identity, args.keychain)
    profile = plistlib.loads(run("security", "cms", "-D", "-i", str(args.app / "Contents/embedded.provisionprofile")))
    team = validate_profile(profile)
    require(entitlements(args.app).get("com.apple.developer.team-identifier") == team, "Signed app/profile team mismatch.")
    name = f"LocalMorph-{args.version}-{args.build}.pkg"
    destination = args.output / name
    run("productbuild", "--sign", args.installer, "--keychain", args.keychain,
        "--component", str(args.app), "/Applications", str(destination))
    signature = run("pkgutil", "--check-signature", str(destination)).decode()
    require(args.installer in signature and team in signature, "Installer signature does not match the selected identity.")
    metadata.update({"package": name, "sha256": digest(destination), "team": team})
    (args.output / "manifest.json").write_text(json.dumps(metadata, indent=2) + "\n")
    shutil.copytree(args.media, args.output / "media-evidence", ignore=shutil.ignore_patterns("ffmpeg-*", "bin", "install", "sources"))
    archive = args.media.parent / "ffmpeg-9.0.2.tar.xz"
    require(archive.is_file() and digest(archive) == SOURCE_SHA256, "Missing exact corresponding FFmpeg source archive.")
    shutil.copyfile(archive, args.output / archive.name)
    for line in (args.media / "source.txt").read_text().splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  (\S+\.tar\.(?:xz|gz))  (https://\S+)", line)
        if not match:
            continue
        dependency = args.media / "sources" / match[2]
        require(dependency.is_file() and digest(dependency) == match[1],
                f"Missing exact corresponding source for {match[2]}.")
        (args.output / "sources").mkdir(exist_ok=True)
        shutil.copyfile(dependency, args.output / "sources" / match[2])
    shutil.copyfile(ROOT / "scripts/mac/build-proof-media.sh", args.output / "build-proof-media.sh")
    symbols = args.output / "symbols"
    symbols.mkdir()
    executable = args.app / "Contents/MacOS" / plistlib.loads((args.app / "Contents/Info.plist").read_bytes())["CFBundleExecutable"]
    uuid = run("dwarfdump", "--uuid", str(executable)).decode().split()[1]
    matched = False
    for path in args.symbols.rglob("*.dSYM"):
        if uuid in run("dwarfdump", "--uuid", str(path)).decode():
            shutil.copytree(path, symbols / path.name)
            matched = True
            break
    require(matched, "A matching native app dSYM is required.")
    for path in (args.app / "Contents").rglob("*.pdb"):
        relative = path.relative_to(args.app / "Contents")
        target = symbols / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, target)
    metadata["files"] = {str(path.relative_to(args.output)): digest(path)
                         for path in args.output.rglob("*") if path.is_file() and path.name != "manifest.json"}
    (args.output / "manifest.json").write_text(json.dumps(metadata, indent=2) + "\n")
    verify_manifest(args.output, args.source, args.version, args.build)
    with open(os.environ["GITHUB_OUTPUT"], "a") as output:
        output.write(f"sha256={metadata['sha256']}\n")
    print(f"Verified Mac Store beta package: {name} SHA256 {metadata['sha256']}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("approval")
    for name in ("signing", "cleanup"):
        command = commands.add_parser(name)
        command.add_argument("directory", type=Path)
    for name in ("package", "verify", "stage"):
        command = commands.add_parser(name)
        for argument in ("version", "build", "source"):
            command.add_argument("--" + argument, required=True)
        if name == "verify":
            command.add_argument("--directory", required=True, type=Path)
        else:
            command.add_argument("--app", required=True, type=Path)
            command.add_argument("--media", required=True, type=Path)
            command.add_argument("--identity", required=True)
            command.add_argument("--keychain", required=name == "package")
            if name == "package":
                command.add_argument("--installer", required=True)
                command.add_argument("--output", required=True, type=Path)
                command.add_argument("--symbols", required=True, type=Path)
    args = parser.parse_args()
    if args.command == "approval":
        approval()
    elif args.command == "signing":
        signing(args.directory)
    elif args.command == "cleanup":
        cleanup(args.directory)
    elif args.command == "package":
        package(args)
    elif args.command == "stage":
        stage(args.app, args.media, args.version, args.build, args.source, args.identity, args.keychain)
    else:
        package_path, manifest = verify_manifest(args.directory, args.source, args.version, args.build)
        require(manifest["sha256"] == os.environ["PACKAGE_SHA256"]
                and manifest["team"] == os.environ["PACKAGE_TEAM"], "Downloaded artifact differs from the signing job's outputs.")
        for name, expected in manifest["files"].items():
            path = (args.directory / name).resolve()
            require(path.is_relative_to(args.directory.resolve()) and path.is_file()
                    and digest(path) == expected, "Artifact inventory/hash mismatch.")
        signature = run("pkgutil", "--check-signature", str(package_path)).decode()
        require(manifest["team"] in signature and re.search(
            r"(?:3rd Party Mac Developer Installer|Mac Installer Distribution):", signature),
            "Downloaded package is not signed by the recorded Store installer team.")
        with open(os.environ["GITHUB_OUTPUT"], "a") as output:
            output.write(f"package={package_path}\n")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError) as error:
        print(f"Mac Store release failed: {error}", file=sys.stderr)
        sys.exit(1)
