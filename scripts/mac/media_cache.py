#!/usr/bin/env python3
"""Cache only source-built media, never applications or signing material."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

from store_release import ROOT, require, validate_media


CACHE = ROOT / "artifacts/mac-media"
BUILDER = ROOT / "scripts/mac/build-proof-media.sh"
ARCHIVE = "ffmpeg-9.0.2.tar.xz"
SOURCE_URL = "https://ffmpeg.org/releases/" + ARCHIVE
METADATA = (
    "COPYING.LGPLv2.1", "LICENSE.md", "configure-flags.txt", "source.txt",
    "binaries.sha256", "dependencies.txt", "xcode.txt", "dependency-sources.txt",
    "configure.log", "build.log",
)


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()


def command(*args):
    return subprocess.check_output(args, text=True).strip()


def source_pins():
    script = BUILDER.read_text()
    ffmpeg = re.search(r'^SHA256="([0-9a-f]{64})"$', script, re.MULTILINE)
    require(ffmpeg is not None, "Missing FFmpeg source pin.")
    dependencies = re.findall(
        r'^\s*"([^|"]+)\|[^|"]+\|([0-9a-f]{64})(?:\|[^"]*)?"', script, re.MULTILINE)
    require(len(dependencies) == 6, "Review the cache source inventory when dependencies change.")
    return {ARCHIVE: ffmpeg[1], **dict(dependencies)}


def build_environment():
    # Do not allow host/Homebrew libraries, user compiler flags or credentials into configure.
    return {
        "HOME": os.environ["HOME"],
        "PATH": "/usr/bin:/bin:/usr/sbin:/sbin",
        "TMPDIR": os.environ["RUNNER_TEMP"],
        "DEVELOPER_DIR": command("xcode-select", "-p"),
        "SDKROOT": command("xcrun", "--sdk", "macosx", "--show-sdk-path"),
        "LANG": "C",
        "LC_ALL": "C",
    }


def context():
    require(command("uname", "-s") == "Darwin" and command("uname", "-m") == "arm64",
            "Media caches require an Apple Silicon macOS host.")
    inputs = {}
    for directory in ("scripts/mac", "dotnet/packaging/mac"):
        for path in sorted((ROOT / directory).rglob("*")):
            if path.is_file() and "__pycache__" not in path.parts:
                require(not path.is_symlink(), "Cache inputs must not be symlinks.")
                inputs[path.relative_to(ROOT).as_posix()] = digest(path)
    action = ROOT / ".github/actions/mac-media/action.yml"
    inputs[action.relative_to(ROOT).as_posix()] = digest(action)
    tools = {}
    for name in ("clang", "clang++", "ld", "ar", "ranlib", "strip"):
        path = Path(command("xcrun", "--find", name)).resolve(strict=True)
        tools[name] = {"path": str(path), "sha256": digest(path)}
    for name in ("make", "bash", "tar", "shasum", "curl"):
        path = Path("/usr/bin") / name if name != "bash" else Path("/bin/bash")
        tools[name] = {"path": str(path), "sha256": digest(path.resolve(strict=True))}
    environment = build_environment()
    sdk = Path(environment["SDKROOT"])
    return {
        "schema": 1,
        "inputs": inputs,
        "sources": source_pins(),
        "environment": environment,
        "output": str(CACHE),
        "os": command("sw_vers"),
        "kernel": command("uname", "-srv"),
        "architecture": command("uname", "-m"),
        "runner": {name: os.environ.get(name, "") for name in
                   ("RUNNER_OS", "RUNNER_ARCH", "ImageOS", "ImageVersion")},
        "xcode": command("xcodebuild", "-version"),
        "compiler": command("xcrun", "clang", "--version"),
        "sdk_version": command("xcrun", "--sdk", "macosx", "--show-sdk-version"),
        "sdk_build": command("xcrun", "--sdk", "macosx", "--show-sdk-build-version"),
        "sdk_settings": digest(sdk / "SDKSettings.plist"),
        "tools": tools,
    }


def inventory(directory):
    files = {}
    for path in sorted(directory.rglob("*")):
        require(not path.is_symlink(), "Symlinks are not permitted in media caches.")
        if path.is_file() and path != directory / "cache.json":
            files[path.relative_to(directory).as_posix()] = digest(path)
    return files


def verify_inventory(directory, expected):
    manifest = json.loads((directory / "cache.json").read_text())
    require(manifest["context"] == expected, "Media cache inputs/toolchain do not match.")
    require(re.fullmatch(r"[0-9a-f]{40}", manifest["producer_commit"]) is not None,
            "Missing media producer commit.")
    require(inventory(directory) == manifest["files"], "Media cache file inventory/hash mismatch.")
    allowed = {ARCHIVE}
    allowed.update("media/sources/" + name for name in source_pins() if name != ARCHIVE)
    allowed.update("media/arm64/" + name for name in METADATA)
    allowed.update("media/arm64/bin/" + name for name in ("ffmpeg", "ffprobe"))
    from store_release import DEPENDENCY_LICENSES
    allowed.update("media/arm64/licenses/" + name for name in DEPENDENCY_LICENSES)
    require(set(manifest["files"]) == allowed, "Unexpected or missing media cache payload.")
    for name, sha256 in source_pins().items():
        path = directory / name if name == ARCHIVE else directory / "media/sources" / name
        require(digest(path) == sha256, f"Cached source does not match its pin: {name}")
    return manifest


def verify(directory, expected):
    verify_inventory(directory, expected)
    media = directory / "media/arm64"
    validate_media(media)
    require((media / "xcode.txt").read_text().strip() == expected["xcode"],
            "Media producer Xcode does not match.")
    for name in ("ffmpeg", "ffprobe"):
        tool = media / "bin" / name
        build = command("xcrun", "vtool", "-show-build", str(tool))
        require(re.search(r"^\s*platform MACOS\s*$", build, re.MULTILINE) is not None
                and re.findall(r"^\s*minos (\S+)", build, re.MULTILINE) == ["15.0"],
                "Cached media must target macOS 15.0, arm64 only.")
        signature = subprocess.run(["codesign", "-dvv", str(tool)], capture_output=True, text=True)
        details = signature.stdout + signature.stderr
        require((signature.returncode == 1 and "not signed at all" in details)
                or (signature.returncode == 0 and "Signature=adhoc" in details
                    and "TeamIdentifier=not set" in details and "Authority=" not in details),
                "Identity-signed media must never enter this cache.")
    filters = command(str(media / "bin/ffmpeg"), "-hide_banner", "-filters")
    names = {line.split()[1] for line in filters.splitlines() if len(line.split()) >= 2}
    require({"subtitles", "ass", "zscale"} <= names, "Cached media lacks required filters.")


def build(expected):
    require(not CACHE.exists(), "Refusing to overwrite a restored or existing media cache.")
    with tempfile.TemporaryDirectory(dir=os.environ["RUNNER_TEMP"]) as temporary:
        work = Path(temporary)
        source = work / ARCHIVE
        subprocess.run(["curl", "--fail", "--location", "--retry", "3", SOURCE_URL,
                        "-o", str(source)], check=True, env=expected["environment"])
        subprocess.run(["bash", str(BUILDER), str(source), str(work / "media"), "arm64"],
                       check=True, env=expected["environment"])
        media = CACHE / "media/arm64"
        media.mkdir(parents=True)
        shutil.copy2(source, CACHE / ARCHIVE)
        shutil.copytree(work / "media/sources", CACHE / "media/sources")
        for name in ("bin", "licenses"):
            shutil.copytree(work / "media/arm64" / name, media / name)
        for name in METADATA:
            shutil.copy2(work / "media/arm64" / name, media / name)
        # Consumers use shasum -c, so rebase the original temporary build paths.
        (media / "binaries.sha256").write_text("".join(
            f"{digest(media / 'bin' / name)}  {media / 'bin' / name}\n"
            for name in ("ffmpeg", "ffprobe")))
        manifest = {
            "context": expected,
            "producer_commit": command("git", "rev-parse", "HEAD"),
            "files": inventory(CACHE),
        }
        (CACHE / "cache.json").write_text(json.dumps(manifest, indent=2) + "\n")
    verify(CACHE, expected)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("key", "build", "verify"))
    args = parser.parse_args()
    expected = context()
    if args.mode == "key":
        with open(os.environ["GITHUB_OUTPUT"], "a") as output:
            output.write("key=" + hashlib.sha256(canonical(expected)).hexdigest() + "\n")
    elif args.mode == "build":
        build(expected)
    else:
        verify(CACHE, expected)


if __name__ == "__main__":
    main()
