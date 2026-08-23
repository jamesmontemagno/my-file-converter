#!/usr/bin/env bash
# Creates the per-platform release tags that trigger .github/workflows/mac-release.yml and
# windows-release.yml. Example: ./scripts/create-release-tags.sh v1.0.0 --mac --windows --push
set -euo pipefail

VERSION="${1:-}"; shift || true
MAC=0; WINDOWS=0; PUSH=0
for arg in "$@"; do
  case "$arg" in
    --mac) MAC=1 ;;
    --windows) WINDOWS=1 ;;
    --push) PUSH=1 ;;
    *) echo "Unknown option: $arg" >&2; exit 1 ;;
  esac
done

if [[ ! "$VERSION" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
  echo "Version must match v<major>.<minor>.<patch>[.<revision>] (example: v1.0.0)." >&2; exit 1
fi
if [[ $MAC -eq 0 && $WINDOWS -eq 0 ]]; then
  echo "Select at least one platform with --mac and/or --windows." >&2; exit 1
fi

TAGS=()
[[ $MAC -eq 1 ]] && TAGS+=("$VERSION-mac")
[[ $WINDOWS -eq 1 ]] && TAGS+=("$VERSION-windows")

for tag in "${TAGS[@]}"; do
  if git rev-parse -q --verify "refs/tags/$tag" >/dev/null; then echo "Tag '$tag' already exists locally." >&2; exit 1; fi
  if [[ -n "$(git ls-remote --tags origin "refs/tags/$tag")" ]]; then echo "Tag '$tag' already exists on origin." >&2; exit 1; fi
  if [[ -f dotnet/CHANGELOG.md ]] && ! grep -Eq "^## \[?$tag" dotnet/CHANGELOG.md; then
    echo "warning: dotnet/CHANGELOG.md has no '## $tag' section; release notes will be generic." >&2
  fi
done

for tag in "${TAGS[@]}"; do git tag -a "$tag" -m "Release $tag"; done
if [[ $PUSH -eq 1 ]]; then git push origin "${TAGS[@]}"; fi

echo "Created tags:"; printf -- '- %s\n' "${TAGS[@]}"
[[ $PUSH -eq 0 ]] && echo "Push with: git push origin ${TAGS[*]}"
