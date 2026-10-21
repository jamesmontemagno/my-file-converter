#!/usr/bin/env bash
# Builds the bundled Mac FFmpeg/FFprobe (LGPL, no network) with static libass and zimg
# from pinned sources. Component/legal approval is tracked separately (#39).
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

# Pinned static dependencies for subtitle burn-in (libass) and HDR tone mapping (zimg/zscale).
# Each archive is the exact corresponding source shipped beside the package. Licenses:
# FreeType FTL, FriBidi LGPL-2.1, HarfBuzz MIT, libass ISC, zimg WTFPL; pkgconf is build-only.
DEPENDENCIES=(
  "pkgconf-2.4.3.tar.xz|https://distfiles.ariadne.space/pkgconf/pkgconf-2.4.3.tar.xz|51203d99ed573fa7344bf07ca626f10c7cc094e0846ac4aa0023bd0c83c25a41"
  "freetype-2.13.3.tar.xz|https://download-mirror.savannah.gnu.org/releases/freetype/freetype-2.13.3.tar.xz|0550350666d427c74daeb85d5ac7bb353acba5f76956395995311a9c6f063289|https://downloads.sourceforge.net/project/freetype/freetype2/2.13.3/freetype-2.13.3.tar.xz"
  "fribidi-1.0.16.tar.xz|https://github.com/fribidi/fribidi/releases/download/v1.0.16/fribidi-1.0.16.tar.xz|1b1cde5b235d40479e91be2f0e88a309e3214c8ab470ec8a2744d82a5a9ea05c"
  "harfbuzz-11.2.1.tar.xz|https://github.com/harfbuzz/harfbuzz/releases/download/11.2.1/harfbuzz-11.2.1.tar.xz|093714c8548a285094685f0bdc999e202d666b59eeb3df2ff921ab68b8336a49"
  "libass-0.17.4.tar.xz|https://github.com/libass/libass/releases/download/0.17.4/libass-0.17.4.tar.xz|78f1179b838d025e9c26e8fef33f8092f65611444ffa1bfc0cfac6a33511a05a"
  "zimg-release-3.0.6.tar.gz|https://github.com/sekrit-twc/zimg/archive/refs/tags/release-3.0.6.tar.gz|be89390f13a5c9b2388ce0f44a5e89364a20c1c57ce46d382b1fcc3967057577"
)
SOURCES="$OUTPUT/sources"
mkdir -p "$SOURCES"
for entry in "${DEPENDENCIES[@]}"; do
  IFS='|' read -r name url hash mirror <<< "$entry"
  archive="$SOURCES/$name"
  if [[ ! -f "$archive" ]]; then
    archive="$archive.partial"
    downloaded=false
    for candidate in "$url" "$mirror"; do
      [[ -n "$candidate" ]] || continue
      echo "Downloading dependency $name from $candidate" >&2
      if curl --fail --silent --show-error --location --retry 3 --retry-max-time 120 \
          --connect-timeout 15 --max-time 60 "$candidate" -o "$archive"; then
        downloaded=true
        url="$candidate"
        break
      fi
    done
    if [[ "$downloaded" != true ]]; then
      echo "Could not download dependency $name from its configured sources." >&2
      exit 1
    fi
  fi
  if [[ "$(shasum -a 256 "$archive" | cut -d ' ' -f 1)" != "$hash" ]]; then
    echo "Dependency source $name does not match its pin." >&2
    exit 1
  fi
  if [[ "$archive" != "$SOURCES/$name" ]]; then
    mv "$archive" "$SOURCES/$name"
  fi
  printf '%s  %s  %s\n' "$hash" "$name" "$url" >> "$BUILD/dependency-sources.txt"
done

DEPS="$BUILD/deps"
HOST="$BUILD/host-tools"
WORK="$BUILD/deps-src"
mkdir -p "$DEPS/lib/pkgconfig" "$DEPS/include" "$HOST" "$WORK" "$BUILD/licenses"
DEP_FLAGS="-arch $ARCH -mmacosx-version-min=15.0 -O2"
step() {
  local name="$1"; shift
  "$@" > "$BUILD/deps-$name.log" 2>&1 || { tail -n 30 "$BUILD/deps-$name.log" >&2; exit 1; }
}
for entry in "${DEPENDENCIES[@]}"; do
  tar -xf "$SOURCES/${entry%%|*}" -C "$WORK"
done

# Build-only pkg-config; restricted to the private prefix so host libraries never leak in.
(cd "$WORK/pkgconf-2.4.3" && step pkgconf sh -c "./configure --prefix='$HOST' --disable-shared && make -j4 && make install")
export PKG_CONFIG="$HOST/bin/pkgconf" PKG_CONFIG_LIBDIR="$DEPS/lib/pkgconfig" PKG_CONFIG_PATH=""
export CFLAGS="$DEP_FLAGS" CXXFLAGS="$DEP_FLAGS" LDFLAGS="-arch $ARCH -mmacosx-version-min=15.0" CC=clang CXX=clang++
STATIC=(--prefix="$DEPS" --host=aarch64-apple-darwin --enable-static --disable-shared)

(cd "$WORK/freetype-2.13.3" && step freetype sh -c "./configure ${STATIC[*]} --with-zlib=no --with-bzip2=no --with-png=no --with-harfbuzz=no --with-brotli=no && make -j4 && make install")
cp "$WORK/freetype-2.13.3/docs/FTL.TXT" "$BUILD/licenses/FreeType-FTL.txt"

(cd "$WORK/fribidi-1.0.16" && step fribidi sh -c "./configure ${STATIC[*]} --disable-docs && make -j4 && make install")
cp "$WORK/fribidi-1.0.16/COPYING" "$BUILD/licenses/FriBidi-COPYING.txt"

# HarfBuzz's supported single-file amalgamation avoids a meson/cmake host requirement.
HB="$WORK/harfbuzz-11.2.1"
(cd "$HB/src" && step harfbuzz sh -c "clang++ $DEP_FLAGS -std=c++17 -fno-exceptions -fno-rtti -DHAVE_PTHREAD=1 -c harfbuzz.cc -o '$WORK/harfbuzz.o' && ar rcs '$DEPS/lib/libharfbuzz.a' '$WORK/harfbuzz.o'")
mkdir -p "$DEPS/include/harfbuzz"
cp "$HB"/src/hb.h "$HB"/src/hb-*.h "$DEPS/include/harfbuzz/"
rm -f "$DEPS/include/harfbuzz/"hb-{coretext,directwrite,gdi,glib,gobject,graphite2,icu,uniscribe,wasm,cairo,ft,gpu}*.h
cat > "$DEPS/lib/pkgconfig/harfbuzz.pc" <<EOF
prefix=$DEPS
Name: harfbuzz
Description: HarfBuzz text shaping library
Version: 11.2.1
Libs: -L\${prefix}/lib -lharfbuzz -lc++
Cflags: -I\${prefix}/include/harfbuzz
EOF
cp "$HB/COPYING" "$BUILD/licenses/HarfBuzz-COPYING.txt"

# libass renders with CoreText system fonts; no fontconfig or font downloads.
(cd "$WORK/libass-0.17.4" && step libass sh -c "./configure ${STATIC[*]} --disable-fontconfig --enable-coretext --disable-libunibreak --disable-asm && make -j4 && make install")
cp "$WORK/libass-0.17.4/COPYING" "$BUILD/licenses/libass-COPYING.txt"

# zimg's autotools sources need autoreconf; compile its fixed source set directly instead.
ZIMG="$WORK/zimg-release-3.0.6"
mkdir -p "$WORK/zimg-obj"
ZIMG_FILES=()
while IFS= read -r file; do ZIMG_FILES+=("$file"); done < <(cd "$ZIMG" && find src/zimg -name '*.cpp' -not -path '*/x86/*' | sort)
(cd "$ZIMG" && step zimg sh -c "for f in ${ZIMG_FILES[*]}; do clang++ $DEP_FLAGS -std=c++14 -DZIMG_ARM -Isrc/zimg -c \"\$f\" -o '$WORK/zimg-obj/'\$(echo \"\$f\" | tr / _).o || exit 1; done && ar rcs '$DEPS/lib/libzimg.a' '$WORK'/zimg-obj/*.o")
cp "$ZIMG/src/zimg/api/zimg.h" "$ZIMG/src/zimg/api/zimg++.hpp" "$DEPS/include/"
cat > "$DEPS/lib/pkgconfig/zimg.pc" <<EOF
prefix=$DEPS
Name: zimg
Description: Scaling, colorspace conversion, and dithering library
Version: 3.0.6
Libs: -L\${prefix}/lib -lzimg -lc++
Cflags: -I\${prefix}/include
EOF
cp "$ZIMG/COPYING" "$BUILD/licenses/zimg-COPYING.txt"
unset CFLAGS CXXFLAGS LDFLAGS CC CXX

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
  --enable-libass
  --enable-libzimg
  --pkg-config="$PKG_CONFIG"
  --pkg-config-flags=--static
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
for filter in subtitles ass zscale; do
  "$BUILD/bin/ffmpeg" -hide_banner -filters 2>/dev/null | awk '{print $2}' | grep -qx "$filter" || {
    echo "Bundled FFmpeg is missing the $filter filter." >&2
    exit 1
  }
done
printf 'Source: https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz\nSHA256: %s\n\nStatic dependencies (SHA256, archive, URL):\n' "$SHA256" > "$BUILD/source.txt"
cat "$BUILD/dependency-sources.txt" >> "$BUILD/source.txt"
xcodebuild -version > "$BUILD/xcode.txt"
echo "Development proof tools built at $BUILD/bin. Source/component approval and shipping compliance remain separate gates."
