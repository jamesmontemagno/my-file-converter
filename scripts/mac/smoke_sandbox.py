"""Launch only the isolated sandbox proof and require its native runtime evidence."""

import argparse
import os
from pathlib import Path
import plistlib
import signal
import subprocess
import time

from validate_app import validate_app


MARKERS = (
    "APPLE_SOFTWARE_PROOF_PASSED",
    "CANCELLATION_PROOF_PASSED",
    "CONTAINER_PROOF_PASSED",
)


def smoke(app, output, timeout=240):
    validate_app(app)
    with (app / "Contents/Info.plist").open("rb") as source:
        info = plistlib.load(source)
    if info.get("CFBundleIdentifier") not in (
        "com.refractored.localmorph.sandboxproof",
        "com.refractored.localmorph.sandboxproof.ci",
    ):
        raise ValueError("Refusing to launch anything except the isolated sandbox proof.")
    output.mkdir(parents=True, exist_ok=False)
    log = output / "native-runtime.log"
    executable = app / "Contents/MacOS" / info["CFBundleExecutable"]
    with log.open("w") as capture:
        process = subprocess.Popen(
            [str(executable)], stdout=capture, stderr=subprocess.STDOUT,
            start_new_session=True,
        )
        try:
            deadline = time.monotonic() + timeout
            while time.monotonic() < deadline:
                text = log.read_text(errors="replace")
                if "PROOF_FAILED" in text or "OPERATION_CANCELED" in text:
                    raise RuntimeError(f"Native proof failed. See {log}")
                if all(marker in text for marker in MARKERS):
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
    print(f"Native scene, Apple encoding, hard-cap and cancellation proof passed. Evidence: {log}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=Path)
    parser.add_argument("output", type=Path)
    arguments = parser.parse_args()
    try:
        smoke(arguments.app.resolve(), arguments.output.resolve())
    except (OSError, ValueError, RuntimeError, TimeoutError, subprocess.SubprocessError) as error:
        parser.exit(1, f"Sandbox runtime verification failed: {error}\n")
