#!/usr/bin/env bash
# Run after a real macOS ios-arm64 Release build. No signing identity is needed.
set -euo pipefail

cd "$(dirname "$0")/.."
test "$(uname -s)" = Darwin
test -n "${GITHUB_SHA:-}"
test -n "${GITHUB_RUN_ID:-}"

output="$PWD/artifacts/ios-release"
mkdir -p "$output"
app_root="$PWD/AliceVision.iOS/bin/Release/net10.0-ios/ios-arm64"
test -d "$app_root"

# .NET creates its device .app directly under the RID directory. Do not search
# a simulator directory or accidentally package a nested framework/extension.
apps=()
while IFS= read -r -d '' candidate; do
  apps+=("$candidate")
done < <(find "$app_root" -maxdepth 1 -type d -name '*.app' -print0)
if [ "${#apps[@]}" -ne 1 ]; then
  printf 'Expected one device .app in %s; found %s\n' "$app_root" "${#apps[@]}" >&2
  exit 1
fi

staging="$(mktemp -d "$RUNNER_TEMP/alice-ipa.XXXXXX")"
# The disposable directory is a newly created absolute child of RUNNER_TEMP.
trap 'rm -rf -- "$staging"' EXIT
mkdir -p "$staging/Payload"
ditto --norsrc --noextattr --noacl "${apps[0]}" "$staging/Payload/$(basename "${apps[0]}")"

ipa="$output/AliceVision-iPhone-unsigned.ipa"
ditto -c -k --norsrc --noextattr --noacl --keepParent "$staging/Payload" "$ipa"
python3 scripts/verify-ipa.py "$ipa" --metadata-out "$output/build-info.json"
(
  cd "$output"
  shasum -a 256 AliceVision-iPhone-unsigned.ipa build-info.json > SHA256SUMS.txt
)
cp scripts/IOS-RELEASE-NOTES.md "$output/RELEASE-NOTES.md"
python3 - "$output/build-info.json" "$output/RELEASE-NOTES.md" <<'PY'
import json
import pathlib
import sys

info = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
with pathlib.Path(sys.argv[2]).open('a', encoding='utf-8') as notes:
    notes.write('\n\n### 本次构建\n\n')
    notes.write(f"- 源码提交：`{info['commit']}`\n")
    notes.write(f"- 版本：{info['version']} / 构建 {info['build']}\n")
    notes.write(f"- 最低系统：iOS {info['minimum_ios']}\n")
    notes.write(f"- 构建时间（UTC）：{info['created_utc']}\n")
    notes.write(f"- [自动构建记录]({info['workflow_url']})\n")
    notes.write(f"- IPA SHA-256：`{info['sha256']}`\n")
PY
cat "$output/SHA256SUMS.txt"
