"""Guard the isolated workspace's platform and entitlement boundaries."""

from pathlib import Path
import plistlib
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "dotnet/src/LocalMorph.App"


class MacWorkspaceTests(unittest.TestCase):
    def test_store_entitlements_do_not_inherit_development_exceptions(self):
        with (APP / "Platforms/MacCatalyst/Store.entitlements").open("rb") as source:
            self.assertEqual(plistlib.load(source), {
                "com.apple.security.app-sandbox": True,
                "com.apple.security.files.user-selected.read-write": True,
                "com.apple.security.files.bookmarks.app-scope": True,
            })

    def test_mac_workspace_has_separate_identity_output_and_explicit_entitlements(self):
        project = ET.parse(APP / "LocalMorph.App.csproj").getroot()
        group = next(group for group in project.findall("PropertyGroup")
                     if group.attrib.get("Condition") == "'$(LocalMorphMacStoreBuild)' == 'true'")
        self.assertEqual(group.findtext("ApplicationId"),
                         "com.refractored.localmorph.sandboxworkspace")
        self.assertIn("mac-store", group.findtext("OutputPath"))
        self.assertEqual(group.findtext("CodesignEntitlements"),
                         "Platforms/MacCatalyst/Store.entitlements")
        self.assertIsNone(group.find("RuntimeIdentifiers"))
        self.assertIsNone(group.find("WindowsAppSDKSelfContained"))

    def test_windows_store_rids_are_platform_scoped(self):
        project = ET.parse(APP / "LocalMorph.App.csproj").getroot()
        group = next(group for group in project.findall("PropertyGroup")
                     if group.find("WindowsAppSDKSelfContained") is not None)
        self.assertIn("== 'windows'", group.attrib["Condition"])
        self.assertEqual(group.findtext("RuntimeIdentifiers"), "win-x64;win-arm64")

    def test_shipping_and_flavor_conflicts_fail_closed(self):
        project = ET.parse(APP / "LocalMorph.App.csproj").getroot()
        target = project.find("./Target[@Name='ValidateMacStoreBuild']")
        self.assertIsNotNone(target)
        errors = target.findall("Error")
        self.assertTrue(any("$(LocalMorphMacSandboxProof)" in error.attrib["Condition"]
                            for error in errors))
        self.assertTrue(any("$(Configuration)" in error.attrib["Condition"]
                            and "Release packaging is blocked" in error.attrib["Text"]
                            for error in errors))
        self.assertTrue(any("$(LocalMorphMacStoreBetaRelease)" in error.attrib["Condition"] for error in errors))
        self.assertTrue(any("$(LocalMorphMacStoreMediaDirectory)" in error.attrib["Condition"] for error in errors))
        self.assertEqual("true", project.findtext("./PropertyGroup/UseInterpreter"))
        removal = project.find("./ItemGroup/CustomEntitlements[@Include='com.apple.security.cs.allow-jit']")
        self.assertEqual("Remove", removal.attrib["Type"])
        privacy = project.find("./ItemGroup/BundleResource[@LogicalName='PrivacyInfo.xcprivacy']")
        self.assertEqual("Platforms/iOS/Resources/PrivacyInfo.xcprivacy", privacy.attrib["Include"])
        self.assertIsNotNone(project.find("./Target[@Name='ValidateWindowsStorePlatform']"))


if __name__ == "__main__":
    unittest.main()
