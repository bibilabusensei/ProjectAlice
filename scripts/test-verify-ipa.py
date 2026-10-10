#!/usr/bin/env python3
"""Exercise IPA publication guards using tiny synthetic ZIP fixtures only."""
import importlib.util
import pathlib
import plistlib
import stat
import struct
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location('ipa_verifier', pathlib.Path(__file__).with_name('verify-ipa.py'))
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)


class PublicationGuardTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='alice-ipa-verifier-tests-')
        self.addCleanup(self.temp.cleanup)
        self.path = pathlib.Path(self.temp.name) / 'fixture.ipa'

    def fixture(self, *, plist_updates=None, cpu=0x0100000C, platform=2, kind=2,
                executable_mode=0o755, extra_file=None, truncate=False):
        info = {
            'CFBundlePackageType': 'APPL', 'DTPlatformName': 'iphoneos',
            'CFBundleSupportedPlatforms': ['iPhoneOS'],
            'NSCameraUsageDescription': '本地空间扫描',
            'CFBundleIdentifier': 'com.projectalice.vision',
            'CFBundleExecutable': 'AliceVision', 'UIDeviceFamily': [1, 2],
            'MinimumOSVersion': '15.0', 'CFBundleVersion': '1',
            'CFBundleShortVersionString': '0.0.2',
            'UISupportedInterfaceOrientations': ['UIInterfaceOrientationPortrait',
                'UIInterfaceOrientationLandscapeLeft', 'UIInterfaceOrientationLandscapeRight'],
        }
        info.update(plist_updates or {})
        header = struct.pack('<8I', 0xFEEDFACF, cpu, 0, kind, 1, 24, 0, 0)
        command = struct.pack('<6I', 0x32, 24, platform, 15 << 16, 26 << 16, 0)
        binary = header + command
        if truncate:
            binary = binary[:40]
        with zipfile.ZipFile(self.path, 'w') as archive:
            archive.writestr('Payload/AliceVision.app/Info.plist', plistlib.dumps(info))
            entry = zipfile.ZipInfo('Payload/AliceVision.app/AliceVision')
            entry.create_system = 3
            entry.external_attr = (stat.S_IFREG | executable_mode) << 16
            archive.writestr(entry, binary)
            if extra_file:
                archive.writestr(extra_file, b'fixture')

    def rejected(self, **kwargs):
        self.fixture(**kwargs)
        with self.assertRaises((ValueError, KeyError)):
            verifier.inspect_ipa(self.path)

    def test_device_package_passes(self):
        self.fixture()
        info = verifier.inspect_ipa(self.path)
        self.assertEqual(info['architecture'], 'arm64')
        self.assertEqual(info['minimum_ios'], '15.0')

    def test_simulator_binary_is_rejected(self):
        self.rejected(platform=7)

    def test_x64_binary_is_rejected(self):
        self.rejected(cpu=0x01000007)

    def test_library_instead_of_app_is_rejected(self):
        self.rejected(kind=6)

    def test_truncated_binary_is_rejected(self):
        self.rejected(truncate=True)

    def test_missing_camera_permission_is_rejected(self):
        self.rejected(plist_updates={'NSCameraUsageDescription': ''})

    def test_missing_landscape_is_rejected(self):
        self.rejected(plist_updates={'UISupportedInterfaceOrientations': ['UIInterfaceOrientationPortrait']})

    def test_simulator_plist_is_rejected(self):
        self.rejected(plist_updates={'DTPlatformName': 'iphonesimulator'})

    def test_wrong_bundle_identity_is_rejected(self):
        self.rejected(plist_updates={'CFBundleIdentifier': 'another.app'})

    def test_lost_execute_permission_is_rejected(self):
        self.rejected(executable_mode=0o644)

    def test_profile_in_unsigned_package_is_rejected(self):
        self.rejected(extra_file='Payload/AliceVision.app/embedded.mobileprovision')

    def test_zip_path_escape_is_rejected(self):
        self.rejected(extra_file='Payload/../../outside')

    def test_multiple_apps_are_rejected(self):
        self.rejected(extra_file='Payload/Other.app/Info.plist')


if __name__ == '__main__':
    unittest.main(verbosity=2)
