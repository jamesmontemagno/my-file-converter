"""Test the native smoke runner without launching apps or signaling real processes."""

from pathlib import Path
import plistlib
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/mac"))
import smoke_sandbox


class MacSmokeTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.app = Path(self.directory.name) / "Proof.app"
        (self.app / "Contents").mkdir(parents=True)
        self.output = Path(self.directory.name) / "runtime"
        self.write_info("com.refractored.localmorph.sandboxproof.ci")
        self.validate = patch.object(smoke_sandbox, "validate_app").start()
        self.addCleanup(patch.stopall)
        self.signal = patch.object(smoke_sandbox.os, "killpg", create=True).start()
        self.process = Mock(pid=12345)
        self.process.poll.return_value = None
        self.popen = patch.object(smoke_sandbox.subprocess, "Popen").start()

    def write_info(self, identifier):
        with (self.app / "Contents/Info.plist").open("wb") as destination:
            plistlib.dump({
                "CFBundleIdentifier": identifier,
                "CFBundleExecutable": "LocalMorph.App",
            }, destination)

    def launch_with_log(self, text):
        def launch(*args, **kwargs):
            kwargs["stdout"].write(text)
            kwargs["stdout"].flush()
            return self.process
        self.popen.side_effect = launch

    def test_production_identity_is_never_launched(self):
        self.write_info("com.refractored.localmorph")
        with self.assertRaisesRegex(ValueError, "Refusing to launch"):
            smoke_sandbox.smoke(self.app, self.output)
        self.popen.assert_not_called()
        self.signal.assert_not_called()
        self.assertFalse(self.output.exists())

    def test_all_markers_are_required_and_owned_process_is_stopped(self):
        self.launch_with_log("\n".join(smoke_sandbox.MARKERS))
        smoke_sandbox.smoke(self.app, self.output)
        self.validate.assert_called_once_with(self.app)
        self.assertTrue(self.popen.call_args.kwargs["start_new_session"])
        self.signal.assert_called_once_with(12345, smoke_sandbox.signal.SIGTERM)
        self.process.wait.assert_called_once_with(timeout=10)

    def test_failure_marker_overrides_success(self):
        self.launch_with_log("\n".join(smoke_sandbox.MARKERS) + "\nPROOF_FAILED: test")
        with self.assertRaisesRegex(RuntimeError, "Native proof failed"):
            smoke_sandbox.smoke(self.app, self.output)
        self.signal.assert_called_once()

    def test_partial_markers_and_early_exit_are_not_success(self):
        self.launch_with_log("CONTAINER_PROOF_PASSED")
        self.process.poll.return_value = 3
        self.process.returncode = 3
        with self.assertRaisesRegex(RuntimeError, "exited 3"):
            smoke_sandbox.smoke(self.app, self.output)
        self.signal.assert_not_called()

    def test_timeout_stops_only_the_owned_process_group(self):
        self.launch_with_log("")
        with self.assertRaises(TimeoutError):
            smoke_sandbox.smoke(self.app, self.output, timeout=0)
        self.signal.assert_called_once_with(12345, smoke_sandbox.signal.SIGTERM)

    def test_unresponsive_owned_process_is_killed_and_reaped(self):
        self.launch_with_log("\n".join(smoke_sandbox.MARKERS))
        self.process.wait.side_effect = [subprocess.TimeoutExpired("proof", 10), 0]
        smoke_sandbox.smoke(self.app, self.output)
        self.assertEqual(self.signal.call_count, 2)
        self.signal.assert_called_with(12345, smoke_sandbox.signal.SIGKILL)

    def test_workspace_check_still_refuses_the_production_identity(self):
        self.write_info("com.refractored.localmorph")
        with self.assertRaisesRegex(ValueError, "Refusing to launch"):
            smoke_sandbox.smoke(self.app, self.output, workspace=True, theme="Light")
        self.popen.assert_not_called()

    def test_workspace_requires_its_loaded_initialized_theme_marker(self):
        self.write_info(smoke_sandbox.WORKSPACE_ID)
        self.launch_with_log("MAC_WORKSPACE_STARTUP_PASSED:Dark")
        with patch.object(smoke_sandbox.time, "sleep") as sleep:
            smoke_sandbox.smoke(self.app, self.output, workspace=True, theme="Dark")
        self.assertEqual(["-theme", "Dark"], self.popen.call_args.args[0][-2:])
        sleep.assert_called_once_with(5)
        self.signal.assert_called_once()

    def test_workspace_exit_after_loading_is_not_success(self):
        self.write_info(smoke_sandbox.WORKSPACE_ID)
        self.launch_with_log("MAC_WORKSPACE_STARTUP_PASSED:Light")
        self.process.poll.return_value = 3
        self.process.returncode = 3
        with patch.object(smoke_sandbox.time, "sleep"):
            with self.assertRaisesRegex(RuntimeError, "Workspace exited 3 after loading"):
                smoke_sandbox.smoke(self.app, self.output, workspace=True, theme="Light")
        self.signal.assert_not_called()

    def test_wrong_theme_or_proof_only_markers_do_not_pass_workspace_startup(self):
        self.write_info(smoke_sandbox.WORKSPACE_ID)
        self.launch_with_log("\n".join(smoke_sandbox.MARKERS) + "\nMAC_WORKSPACE_STARTUP_PASSED:Dark")
        self.process.poll.return_value = 3
        self.process.returncode = 3
        with self.assertRaisesRegex(RuntimeError, "before completing the proof"):
            smoke_sandbox.smoke(self.app, self.output, workspace=True, theme="Light")

    def test_workspace_preparation_only_resigns_a_copy_and_runs_all_themes(self):
        self.write_info("com.refractored.localmorph")
        original = (self.app / "Contents/Info.plist").read_bytes()
        profile = self.app / "Contents/embedded.provisionprofile"
        profile.write_text("original-profile")
        with patch.object(smoke_sandbox.subprocess, "run") as run:
            with patch.object(smoke_sandbox, "smoke") as smoke:
                smoke_sandbox.workspace_smoke(self.app, self.output)
        copied = self.output / smoke_sandbox.WORKSPACE_APP
        self.assertEqual(original, (self.app / "Contents/Info.plist").read_bytes())
        self.assertEqual("original-profile", profile.read_text())
        self.assertFalse((copied / "Contents/embedded.provisionprofile").exists())
        self.assertEqual(smoke_sandbox.WORKSPACE_ID,
                         plistlib.loads((copied / "Contents/Info.plist").read_bytes())["CFBundleIdentifier"])
        self.assertEqual(str(copied), run.call_args_list[0].args[0][-1])
        self.assertEqual(["Light", "Dark", "Unspecified"],
                         [call.kwargs["theme"] for call in smoke.call_args_list])
        self.assertTrue(all(call.args[0] == copied for call in smoke.call_args_list))

    def test_transferred_workspace_is_verified_and_launched_without_rebuilding_or_resigning(self):
        self.write_info(smoke_sandbox.WORKSPACE_ID)
        with patch.object(smoke_sandbox.subprocess, "run") as run:
            with patch.object(smoke_sandbox, "smoke") as smoke:
                smoke_sandbox.workspace_smoke(self.app, self.output)
        self.assertEqual(["codesign", "--verify", "--deep", "--strict", str(self.app)],
                         run.call_args.args[0])
        run.assert_called_once()
        self.assertEqual(3, smoke.call_count)


if __name__ == "__main__":
    unittest.main()
