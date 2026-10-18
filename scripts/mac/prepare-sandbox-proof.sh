#!/usr/bin/env bash
# Builds an isolated local diagnostic app; never packages or submits a Store release.
set -euo pipefail
if [[ $# -ne 2 ]]; then
  echo "Usage: bash scripts/mac/prepare-sandbox-proof.sh <proof-media-build-directory> <new-output-directory>" >&2
  exit 1
fi
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
MEDIA="$(cd "$1" && pwd)"
OUTPUT="$2"
IDENTITY="${LOCALMORPH_PROOF_SIGNING_IDENTITY:-Apple Development}"
PROOF_ID="com.refractored.localmorph.sandboxproof"
if [[ "$IDENTITY" == "-" ]]; then
  PROOF_ID="com.refractored.localmorph.sandboxproof.ci"
fi
if [[ -e "$OUTPUT" ]]; then
  echo "Refusing to overwrite an existing proof directory: $OUTPUT" >&2
  exit 1
fi
shasum -a 256 -c "$MEDIA/arm64/binaries.sha256"
for tool in ffmpeg ffprobe; do
  if [[ "$(lipo -archs "$MEDIA/arm64/bin/$tool")" != arm64 ]]; then
    echo "Proof media tools must be Apple Silicon only." >&2
    exit 1
  fi
  DEPENDENCIES="$(otool -L "$MEDIA/arm64/bin/$tool")"
  LIBRARIES="$(printf '%s\n' "$DEPENDENCIES" | awk '/^[[:space:]]/{ print $1 }')"
  if [[ -z "$LIBRARIES" ]]; then
    echo "Could not establish the proof tools' system dependencies." >&2
    exit 1
  fi
  if printf '%s\n' "$LIBRARIES" | grep -Ev '^(/System/Library/|/usr/lib/)'; then
    echo "Proof media tools must have only system dependencies." >&2
    exit 1
  fi
done
mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"
cd "$ROOT"
dotnet build dotnet/src/LocalMorph.App/LocalMorph.App.csproj -f net10.0-maccatalyst \
  -p:MacOnly=true -p:LocalMorphMacSandboxProof=true -p:EnableCodeSigning=true \
  "-p:ApplicationId=$PROOF_ID" \
  -p:CodesignRequireProvisioningProfile=false "-p:CodesignKey=$IDENTITY" \
  "-p:BaseOutputPath=$OUTPUT/app/" -v:quiet
APP="$OUTPUT/app/Debug/net10.0-maccatalyst/maccatalyst-arm64/LocalMorph Sandbox Proof.app"
python3 scripts/mac/validate_app.py "$APP"
for tool in ffmpeg ffprobe; do
  cp "$MEDIA/arm64/bin/$tool" "$APP/Contents/MacOS/$tool"
  codesign --force --sign "$IDENTITY" --timestamp=none --options runtime \
    --entitlements dotnet/src/LocalMorph.App/Platforms/MacCatalyst/MediaTool.entitlements "$APP/Contents/MacOS/$tool"
  codesign --verify --strict "$APP/Contents/MacOS/$tool"
done
mkdir -p "$APP/Contents/Resources/FFmpeg"
cp "$MEDIA/arm64/COPYING.LGPLv2.1" "$MEDIA/arm64/LICENSE.md" \
  dotnet/packaging/mac/FFmpeg-proof-NOTICE.txt "$APP/Contents/Resources/FFmpeg/"
codesign --force --sign "$IDENTITY" --timestamp=none \
  --entitlements dotnet/src/LocalMorph.App/Platforms/MacCatalyst/SandboxProof.entitlements "$APP"
codesign --verify --strict "$APP"
shasum -a 256 "$APP/Contents/MacOS/ffmpeg" "$APP/Contents/MacOS/ffprobe" > "$OUTPUT/signed-tools.sha256"
git rev-parse HEAD > "$OUTPUT/base-commit.txt"
git diff --binary > "$OUTPUT/working-tree.patch"
mkdir "$OUTPUT/proof-source"
cp dotnet/src/LocalMorph.App/Platforms/MacCatalyst/SandboxProofApp.cs \
  dotnet/src/LocalMorph.App/Platforms/MacCatalyst/SandboxProof.entitlements \
  dotnet/src/LocalMorph.App/Platforms/MacCatalyst/MediaTool.entitlements \
  dotnet/src/LocalMorph.App/Platforms/MacCatalyst/SceneDelegate.cs \
  dotnet/src/LocalMorph.App/Platforms/MacCatalyst/Info.plist \
  scripts/mac/build-proof-media.sh scripts/mac/prepare-sandbox-proof.sh \
  scripts/mac/validate_app.py scripts/mac/smoke_sandbox.py \
  dotnet/packaging/mac/FFmpeg-proof-NOTICE.txt "$OUTPUT/proof-source/"
shasum -a 256 "$OUTPUT/proof-source/"* > "$OUTPUT/proof-source.sha256"
echo "Prepared local sandbox proof: $APP"
echo "This is an Apple Silicon / macOS 15+ development app, not a Store upload artifact."
