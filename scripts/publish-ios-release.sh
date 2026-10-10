#!/usr/bin/env bash
# GitHub-hosted runner only; GH_TOKEN is the workflow's limited GITHUB_TOKEN.
set -euo pipefail
cd "$(dirname "$0")/.."
test -n "${GH_TOKEN:-}"
test -n "${GITHUB_REPOSITORY:-}"
test -n "${GITHUB_SHA:-}"
test -n "${DEFAULT_BRANCH:-}"

head_sha="$(gh api "repos/$GITHUB_REPOSITORY/commits/$DEFAULT_BRANCH" --jq .sha)"
if [ "$head_sha" != "$GITHUB_SHA" ]; then
  printf 'Skipping stale build %s; default branch is now %s\n' "$GITHUB_SHA" "$head_sha"
  exit 0
fi
python3 - "$GITHUB_SHA" <<'PY'
import json
import pathlib
import sys

info = json.loads(pathlib.Path('artifacts/ios-release/build-info.json').read_text(encoding='utf-8'))
if info.get('commit') != sys.argv[1]:
    raise SystemExit('Refusing to publish artifacts from a different source commit')
PY

tag='alice-ios-preview'
title="AliceVision iPhone 测试版 · ${GITHUB_SHA:0:7}"
# Draft releases do not create backing tags. Ensure the lightweight ref exists
# before creating the draft, including the very first publication.
if gh api "repos/$GITHUB_REPOSITORY/git/ref/tags/$tag" >/dev/null 2>&1; then
  gh api --method PATCH "repos/$GITHUB_REPOSITORY/git/refs/tags/$tag" \
    -f sha="$GITHUB_SHA" -F force=true >/dev/null
else
  gh api --method POST "repos/$GITHUB_REPOSITORY/git/refs" \
    -f ref="refs/tags/$tag" -f sha="$GITHUB_SHA" >/dev/null
fi
if ! gh release view "$tag" --repo "$GITHUB_REPOSITORY" >/dev/null 2>&1; then
  gh release create "$tag" --repo "$GITHUB_REPOSITORY" \
    --target "$GITHUB_SHA" --title "$title" --draft --prerelease \
    --notes-file artifacts/ios-release/RELEASE-NOTES.md
fi
gh release upload "$tag" --repo "$GITHUB_REPOSITORY" --clobber \
  artifacts/ios-release/AliceVision-iPhone-unsigned.ipa \
  artifacts/ios-release/SHA256SUMS.txt \
  artifacts/ios-release/build-info.json \
  artifacts/ios-release/RELEASE-NOTES.md
head_sha="$(gh api "repos/$GITHUB_REPOSITORY/commits/$DEFAULT_BRANCH" --jq .sha)"
if [ "$head_sha" != "$GITHUB_SHA" ]; then
  printf 'Leaving stale upload unpublished: %s; default branch is now %s\n' "$GITHUB_SHA" "$head_sha"
  exit 0
fi
gh release edit "$tag" --repo "$GITHUB_REPOSITORY" --title "$title" \
  --notes-file artifacts/ios-release/RELEASE-NOTES.md --prerelease --draft=false
url="$(gh release view "$tag" --repo "$GITHUB_REPOSITORY" --json url --jq .url)"
printf 'Published %s\n' "$url"
printf '### iPhone 测试版\n\n[下载未签名 IPA](%s)\n\n源码提交：`%s`\n' \
  "$url" "$GITHUB_SHA" >> "$GITHUB_STEP_SUMMARY"
