#!/usr/bin/env bash
# Development feasibility build, not an approved Mac App Store payload.
set -euo pipefail

if [[ $# -ne 3 || "$3" != arm64 ]]; then
  echo "Usage: bash scripts/mac/build-proof-media.sh <ffmpeg-9.0.2.tar.xz> <output-directory> <arm64>" >&2
  exit 1
fi
SOURCE="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
OUTPUT="$2"
ARCH="$3"
SHA256="8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e"
if [[ "$(shasum -a 256 "$SOURCE" | cut -d ' ' -f 1)" != "$SHA256" ]]; then
  echo "FFmpeg source does not match the development proof source pin." >&2
  exit 1
fi
if [[ "$(uname -s)" != Darwin ]]; then
  echo "This proof builder requires macOS and the selected Xcode command-line tools." >&2
  exit 1
fi
mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"
if [[ -e "$OUTPUT/$ARCH" ]]; then
  echo "Refusing to overwrite an existing proof build: $OUTPUT/$ARCH" >&2
  exit 1
fi
mkdir "$OUTPUT/$ARCH"
BUILD="$OUTPUT/$ARCH"
tar -xf "$SOURCE" -C "$BUILD"
cd "$BUILD/ffmpeg-9.0.2"

FLAGS=(
  --prefix="$BUILD/install"
  --cc=clang
  --arch="$ARCH"
  --target-os=darwin
  --enable-cross-compile
  --disable-autodetect
  --disable-gpl
  --disable-nonfree
  --disable-shared
  --enable-static
  --disable-doc
  --disable-debug
  --disable-network
  --disable-ffplay
  --disable-x86asm
  --enable-videotoolbox
  --enable-audiotoolbox
  --enable-zlib
  --extra-cflags="-arch $ARCH -mmacosx-version-min=15.0"
  --extra-ldflags="-arch $ARCH -mmacosx-version-min=15.0"
)
printf '%s\n' "${FLAGS[@]}" > "$BUILD/configure-flags.txt"
./configure "${FLAGS[@]}" > "$BUILD/configure.log" 2>&1 || {
  tail -n 30 "$BUILD/configure.log" >&2
  exit 1
}
make -j4 > "$BUILD/build.log" 2>&1 || {
  tail -n 30 "$BUILD/build.log" >&2
  exit 1
}
mkdir "$BUILD/bin"
cp ffmpeg ffprobe "$BUILD/bin/"
for tool in ffmpeg ffprobe; do
  lipo "$BUILD/bin/$tool" -verify_arch "$ARCH"
done
cp COPYING.LGPLv2.1 LICENSE.md "$BUILD/"
shasum -a 256 "$BUILD/bin/ffmpeg" "$BUILD/bin/ffprobe" > "$BUILD/binaries.sha256"
otool -L "$BUILD/bin/ffmpeg" "$BUILD/bin/ffprobe" > "$BUILD/dependencies.txt"
LIBRARIES="$(awk '/^[[:space:]]/{ print $1 }' "$BUILD/dependencies.txt")"
if [[ -z "$LIBRARIES" ]]; then
  echo "Could not establish the proof tools' system dependencies." >&2
  exit 1
fi
if printf '%s\n' "$LIBRARIES" | grep -Ev '^(/System/Library/|/usr/lib/)'; then
  echo "Proof tools have unexpected non-system dependencies." >&2
  exit 1
fi
printf 'Source: https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz\nSHA256: %s\n' "$SHA256" > "$BUILD/source.txt"
xcodebuild -version > "$BUILD/xcode.txt"
echo "Development proof tools built at $BUILD/bin. Source/component approval and shipping compliance remain separate gates."
