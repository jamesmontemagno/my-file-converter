#!/usr/bin/env bash
# Creates tags for enabled Store workflows only.
# Example: ./scripts/create-release-tags.sh v1.0.0 --windows --push
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

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
if [[ $MAC -eq 1 && ! -f "$ROOT/.github/workflows/mac-store-publish.yml" ]]; then
  echo "Mac App Store delivery is not enabled yet; no tags were created." >&2; exit 1
fi
if [[ $MAC -eq 1 && ! "$VERSION" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
  echo "Mac Store versions require exactly three integer components without leading zeroes." >&2; exit 1
fi
if [[ $WINDOWS -eq 1 && ! -f "$ROOT/.github/workflows/windows-store-publish.yml" ]]; then
  echo "Windows Store delivery is not enabled; no tags were created." >&2; exit 1
fi
if [[ $WINDOWS -eq 1 ]]; then
  IFS='.' read -r -a PARTS <<< "${VERSION#v}"
  for part in "${PARTS[@]}"; do
    if [[ ${#part} -gt 5 ]] || (( 10#$part > 65535 )); then
      echo "Windows Store version components must be between 0 and 65535." >&2; exit 1
    fi
  done
  if (( 10#${PARTS[0]} < 1 )) || [[ ${#PARTS[@]} -eq 4 && ${PARTS[3]} != "0" ]]; then
    echo "Windows Store versions require major >= 1 and a zero fourth component." >&2; exit 1
  fi
fi

cd "$ROOT"
git rev-parse --is-inside-work-tree >/dev/null
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
if [[ $PUSH -eq 0 ]]; then echo "Push with: git push origin ${TAGS[*]}"; fi
