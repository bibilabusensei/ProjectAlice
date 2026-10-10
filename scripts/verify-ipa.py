#!/usr/bin/env python3
"""Reject malformed, simulator, wrong-architecture, or incomplete iPhone IPAs."""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import pathlib
import plistlib
import stat
import struct
import subprocess
import sys
import zipfile


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def inspect_executable(data: bytes) -> dict[str, object]:
    require(len(data) >= 32, 'Executable is truncated')
    # iPhone's mono AOT executable must be a 64-bit little-endian Mach-O ARM64.
    header = struct.unpack_from('<8I', data)
    magic, cpu, _, kind, count, size, _, _ = header
    require(magic == 0xFEEDFACF, 'Executable is not a 64-bit Mach-O binary')
    require(cpu == 0x0100000C, 'Executable is not ARM64 for a physical iPhone')
    require(kind == 2, 'Main binary is not a Mach-O executable')
    require(32 + size <= len(data), 'Mach-O load commands are truncated')
    offset = 32
    platforms: list[int] = []
    legacy_ios = False
    for _ in range(count):
        require(offset + 8 <= 32 + size, 'Invalid Mach-O command header')
        command, length = struct.unpack_from('<2I', data, offset)
        require(length >= 8 and offset + length <= 32 + size, 'Invalid Mach-O command size')
        if command == 0x32:  # LC_BUILD_VERSION: platform 2 is iOS, 7 is simulator.
            require(length >= 24, 'Truncated LC_BUILD_VERSION')
            platforms.append(struct.unpack_from('<I', data, offset + 8)[0])
        elif command == 0x25:  # LC_VERSION_MIN_IPHONEOS (older device binaries).
            legacy_ios = True
        offset += length
    require((platforms and all(value == 2 for value in platforms)) or
            (not platforms and legacy_ios), 'Main binary does not target physical iOS')
    return {'architecture': 'arm64', 'mach_o_platform': 'iOS device'}


def inspect_ipa(path: pathlib.Path) -> dict[str, object]:
    require(path.is_file(), f'IPA does not exist: {path}')
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        names = [entry.filename for entry in entries]
        require(len(names) == len(set(names)), 'IPA contains duplicate filenames')
        require(archive.testzip() is None, 'IPA has a corrupt ZIP entry')
        for name in names:
            parts = pathlib.PurePosixPath(name).parts
            require(not name.startswith('/') and '..' not in parts and '\\' not in name,
                    f'Unsafe ZIP path: {name}')
            require(name.startswith('Payload/'), f'Unexpected top-level IPA file: {name}')
        roots = {name.split('/')[1] for name in names if len(name.split('/')) > 1
                 and name.split('/')[1]}
        require(len(roots) == 1 and next(iter(roots)).endswith('.app'),
                'IPA must contain exactly one Payload/*.app')
        root = 'Payload/' + next(iter(roots)) + '/'
        info = plistlib.loads(archive.read(root + 'Info.plist'))
        require(info.get('CFBundlePackageType') == 'APPL', 'Bundle package type is not APPL')
        require(info.get('DTPlatformName') == 'iphoneos', 'Bundle targets a simulator or another OS')
        require('iPhoneOS' in info.get('CFBundleSupportedPlatforms', []), 'Missing iPhoneOS platform')
        require(bool(info.get('NSCameraUsageDescription', '').strip()), 'Camera permission description is missing')
        require(info.get('CFBundleIdentifier') == 'com.projectalice.vision', 'Unexpected bundle identifier')
        orientations = info.get('UISupportedInterfaceOrientations', [])
        required = {'UIInterfaceOrientationPortrait', 'UIInterfaceOrientationLandscapeLeft',
                    'UIInterfaceOrientationLandscapeRight'}
        require(required.issubset(orientations), 'Portrait/landscape orientations are incomplete')
        minimum = str(info.get('MinimumOSVersion', ''))
        require(minimum and tuple(int(v) for v in minimum.split('.')) >= (15, 0),
                'Missing or unexpected minimum iOS version')
        require(1 in info.get('UIDeviceFamily', []), 'Bundle does not support iPhone')
        require(root + 'embedded.mobileprovision' not in names, 'Unsigned package includes a provisioning profile')
        executable = info.get('CFBundleExecutable', '')
        require(executable and '/' not in executable, 'Missing or invalid main executable name')
        binary_entry = archive.getinfo(root + executable)
        mode = binary_entry.external_attr >> 16
        require(mode & stat.S_IXUSR != 0, 'Main executable lost its execute permission during packaging')
        mach_o = inspect_executable(archive.read(binary_entry))

    return {
        'ipa': path.name,
        'size_bytes': path.stat().st_size,
        'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
        'bundle_identifier': info['CFBundleIdentifier'],
        'version': info.get('CFBundleShortVersionString', ''),
        'build': info.get('CFBundleVersion', ''),
        'minimum_ios': minimum,
        'orientations': orientations,
        'signing': 'unsigned; re-sign before installation',
        **mach_o,
    }


def command_version(command: list[str]) -> str:
    try:
        return subprocess.check_output(command, text=True, stderr=subprocess.STDOUT).strip()
    except (OSError, subprocess.CalledProcessError):
        return 'unavailable'


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('ipa', type=pathlib.Path)
    parser.add_argument('--metadata-out', type=pathlib.Path)
    args = parser.parse_args()
    try:
        info = inspect_ipa(args.ipa)
        if args.metadata_out:
            repository = os.environ.get('GITHUB_REPOSITORY', '')
            run = os.environ.get('GITHUB_RUN_ID', '')
            info.update({
                'commit': os.environ.get('GITHUB_SHA', ''),
                'created_utc': dt.datetime.now(dt.timezone.utc).isoformat(),
                'workflow_url': f'https://github.com/{repository}/actions/runs/{run}',
                'dotnet_sdk': command_version(['dotnet', '--version']),
                'ios_workload_set': '10.0.103',
                'xcode': command_version(['xcodebuild', '-version']),
                'iphone_sdk': command_version(['xcrun', '--sdk', 'iphoneos', '--show-sdk-version']),
            })
            args.metadata_out.write_text(json.dumps(info, indent=2, ensure_ascii=False) + '\n',
                                         encoding='utf-8')
        print(json.dumps(info, indent=2, ensure_ascii=False))
        return 0
    except (ValueError, KeyError, zipfile.BadZipFile, OSError, plistlib.InvalidFileException) as error:
        print(f'IPA verification failed: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
