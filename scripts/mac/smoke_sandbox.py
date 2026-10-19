"""Launch isolated Mac proof/workspace identities and require native runtime evidence."""

import argparse
import os
from pathlib import Path
import plistlib
import signal
import shutil
import subprocess
import time

from validate_app import validate_app


MARKERS = (
    "BOOKMARK_API_PROOF_PASSED",
    "APPLE_SOFTWARE_PROOF_PASSED",
    "CANCELLATION_PROOF_PASSED",
    "CONTAINER_PROOF_PASSED",
)
WORKSPACE_ID = "com.refractored.localmorph.workspacestartup.ci"
WORKSPACE_APP = "LocalMorph Workspace Startup.app"
ROOT = Path(__file__).resolve().parents[2]


def smoke(app, output, timeout=240, *, workspace=False, theme=None):
    validate_app(app)
    with (app / "Contents/Info.plist").open("rb") as source:
        info = plistlib.load(source)
    allowed = (WORKSPACE_ID,) if workspace else (
        "com.refractored.localmorph.sandboxproof",
        "com.refractored.localmorph.sandboxproof.ci",
    )
    if info.get("CFBundleIdentifier") not in allowed:
        raise ValueError("Refusing to launch anything except the selected isolated diagnostic identity.")
    if (workspace and theme not in ("Light", "Dark", "Unspecified")) or (not workspace and theme is not None):
        raise ValueError("Theme checks require an isolated workspace and a valid app theme.")
    markers = (f"MAC_WORKSPACE_STARTUP_PASSED:{theme}",) if workspace else MARKERS
    output.mkdir(parents=True, exist_ok=False)
    log = output / "native-runtime.log"
    executable = app / "Contents/MacOS" / info["CFBundleExecutable"]
    with log.open("w") as capture:
        process = subprocess.Popen(
            [str(executable)] + (["-theme", theme] if theme is not None else []),
            stdout=capture, stderr=subprocess.STDOUT,
            start_new_session=True,
        )
        try:
            deadline = time.monotonic() + timeout
            while time.monotonic() < deadline:
                text = log.read_text(errors="replace")
                if "PROOF_FAILED" in text or "OPERATION_CANCELED" in text:
                    raise RuntimeError(f"Native proof failed. See {log}")
                if all(marker in text for marker in markers):
                    if workspace:
                        time.sleep(5)
                        if process.poll() is not None:
                            raise RuntimeError(f"Workspace exited {process.returncode} after loading. See {log}")
                    break
                if process.poll() is not None:
                    raise RuntimeError(f"App exited {process.returncode} before completing the proof. See {log}")
                time.sleep(1)
            else:
                raise TimeoutError(f"Native proof timed out after {timeout}s. See {log}")
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=10)
    label = f"Release workspace startup ({theme})" if workspace else "Native scene, bookmark API, Apple encoding, hard-cap and cancellation proof"
    print(f"{label} passed. Evidence: {log}")


def workspace_smoke(app, output):
    validate_app(app)
    info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
    if info.get("CFBundleIdentifier") not in ("com.refractored.localmorph", WORKSPACE_ID):
        raise ValueError("Expected a staged Store app or the isolated startup workspace.")
    output.mkdir(parents=True, exist_ok=False)
    if info["CFBundleIdentifier"] == "com.refractored.localmorph":
        # Never launch the production identity or touch its preferences/history/grants.
        isolated = output / WORKSPACE_APP
        shutil.copytree(app, isolated, symlinks=True)
        info["CFBundleIdentifier"] = WORKSPACE_ID
        info["CFBundleDisplayName"] = "LocalMorph Workspace Startup"
        (isolated / "Contents/Info.plist").write_bytes(plistlib.dumps(info))
        (isolated / "Contents/embedded.provisionprofile").unlink(missing_ok=True)
        subprocess.run([
            "codesign", "--force", "--sign", "-", "--options", "runtime",
            "--entitlements", str(ROOT / "dotnet/src/LocalMorph.App/Platforms/MacCatalyst/Store.entitlements"),
            str(isolated),
        ], check=True)
        app = isolated
    subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)], check=True)
    for theme in ("Light", "Dark", "Unspecified"):
        smoke(app, output / theme, workspace=True, theme=theme)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--workspace", action="store_true",
                        help="Check the actual Release UI in a separate sandbox identity, including light/dark/system themes.")
    arguments = parser.parse_args()
    try:
        runner = workspace_smoke if arguments.workspace else smoke
        runner(arguments.app.resolve(), arguments.output.resolve())
    except (OSError, ValueError, RuntimeError, TimeoutError, subprocess.SubprocessError) as error:
        parser.exit(1, f"Sandbox runtime verification failed: {error}\n")
