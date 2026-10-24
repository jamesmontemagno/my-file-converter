#!/usr/bin/env python3
"""Route desktop CI from the complete changed-file set, including deletions."""

import json
import os
from pathlib import Path
import re
import subprocess


def affected(paths):
    result = dict.fromkeys(("distribution", "bridge", "core", "windows", "mac"), False)
    for path in paths:
        if path.endswith(".md"):
            continue
        name = Path(path).name
        if (path in {"scripts/ci_paths.py", ".github/workflows/bridge-ci.yml"}
                or name in {"global.json", "NuGet.config", "nuget.config"}
                or name.startswith("Directory.") and name.endswith((".props", ".targets"))
                or path.startswith("dotnet/") and path.endswith((".sln", ".slnx"))):
            result = dict.fromkeys(result, True)
        elif path.startswith("dotnet/src/LocalMorph.Core/"):
            result = dict.fromkeys(result, True)
        elif path.startswith("dotnet/src/LocalMorph.Bridge/"):
            result.update(distribution=True, bridge=True)
        elif path.startswith("dotnet/tests/LocalMorph.Bridge.Tests/"):
            result["bridge"] = True
        elif path.startswith("dotnet/tests/LocalMorph.Core.Tests/"):
            result["core"] = True
        elif path.startswith("dotnet/tests/"):
            result.update(bridge=True, core=True)
        elif path.startswith("dotnet/src/LocalMorph.App/Platforms/Windows/"):
            result.update(distribution=True, windows=True)
        elif path.startswith("dotnet/src/LocalMorph.App/Platforms/MacCatalyst/"):
            result.update(distribution=True, mac=True)
        elif path == "dotnet/src/LocalMorph.App/Platforms/iOS/Resources/PrivacyInfo.xcprivacy":
            result.update(distribution=True, mac=True)
        elif path.startswith(("dotnet/src/LocalMorph.App/Platforms/iOS/", "dotnet/src/LocalMorph.App/Platforms/Android/")):
            continue
        elif path.startswith("dotnet/src/LocalMorph.App/"):
            result.update(distribution=True, windows=True, mac=True)
        elif (path.startswith(("scripts/windows/", "dotnet/packaging/windows/", "dotnet/src/LocalMorph.Shell/"))
              or path == ".github/workflows/windows-store-publish.yml"):
            result.update(distribution=True, windows=True)
        elif (path.startswith(("scripts/mac/", "dotnet/packaging/mac/", ".github/actions/mac-media/"))
              or path == ".github/workflows/mac-store-publish.yml"):
            result.update(distribution=True, mac=True)
        elif path == ".github/workflows/bridge-release.yml":
            result["bridge"] = True
        elif (path.startswith(("scripts/tests/", "scripts/create-release-tags.", "Casks/",
                               "public/appcast", "dotnet/packaging/winget/"))
              or path in {".github/workflows/mac-release.yml", ".github/workflows/windows-release.yml",
                          ".github/workflows/winget-submit.yml"}):
            result["distribution"] = True
    return result


def changed_paths(event_name, event):
    if event_name == "pull_request":
        base, head = event["pull_request"]["base"]["sha"], event["pull_request"]["head"]["sha"]
        comparison = "..."
    elif event_name == "push":
        base, head, comparison = event["before"], event["after"], ".."
    else:
        raise ValueError(f"Unsupported CI event: {event_name}")
    if not all(re.fullmatch(r"[0-9a-f]{40}", value) for value in (base, head)):
        raise ValueError("Expected immutable Git commit identifiers.")
    if base == "0" * 40:
        command = ["git", "ls-tree", "-r", "--name-only", "-z", head]
    else:
        command = ["git", "diff", "--no-renames", "--name-only", "-z", base + comparison + head]
    return subprocess.check_output(command).decode().split("\0")[:-1]


def main():
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
    result = affected(changed_paths(os.environ["GITHUB_EVENT_NAME"], event))
    with open(os.environ["GITHUB_OUTPUT"], "a") as output:
        for name, selected in result.items():
            output.write(f"{name}={str(selected).lower()}\n")
    print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    main()
