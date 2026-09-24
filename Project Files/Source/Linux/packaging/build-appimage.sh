#!/usr/bin/env bash
# Package the published application (./build.sh --app) as an AppImage:
# one file that runs without installing anything.
#
#   packaging/build-appimage.sh [dist dir] [output dir]
#
# Uses 'appimagetool' from PATH, or $APPIMAGETOOL, or downloads it (with the
# AppImage type-2 runtime) into build/appimage-tools the first time.
# VERSION sets the version in the file name (default: date + git commit).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dist="${1:-$here/dist/thetis}"
out="${2:-$here/dist}"
tools="$here/build/appimage-tools"
appdir="$here/build/Thetis.AppDir"
arch=x86_64

[ -x "$dist/thetis" ] || { echo "no application in $dist; run ./build.sh --app first" >&2; exit 1; }

version="${VERSION:-$(date +%Y%m%d)-$(git -C "$here" rev-parse --short HEAD 2>/dev/null || echo local)}"

# --- AppDir -------------------------------------------------------------------
rm -rf "$appdir"
mkdir -p "$appdir/usr/lib/thetis/lib" "$appdir/usr/lib/thetis/lib-fallback" \
         "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/256x256/apps" \
         "$appdir/usr/share/metainfo"
cp -a "$dist/." "$appdir/usr/lib/thetis/"

# bundled system libraries (see AppRun for why these and not others)
copy_lib() {   # soname dest
    local p
    p="$(ldconfig -p | awk -v n="$1" '$1 == n && /x86-64/ { print $NF; exit }')"
    [ -n "$p" ] || { echo "library $1 not found on the build machine" >&2; exit 1; }
    cp -L "$p" "$2/$1"
}
copy_lib libfftw3.so.3  "$appdir/usr/lib/thetis/lib"
copy_lib libfftw3f.so.3 "$appdir/usr/lib/thetis/lib"
copy_lib libjack.so.0   "$appdir/usr/lib/thetis/lib-fallback"
copy_lib libdb-5.3.so   "$appdir/usr/lib/thetis/lib-fallback"
# drop symbol tables (the native libraries are built without debug info)
strip --strip-unneeded "$appdir"/usr/lib/thetis/lib{wdsp,ChannelMaster,PA19,aethernr}.so \
    "$appdir"/usr/lib/thetis/lib/*.so.* "$appdir"/usr/lib/thetis/lib-fallback/*.so*
# .NET debugger support libraries: not needed to run
rm -f "$appdir"/usr/lib/thetis/{libmscordaccore.so,libmscordbi.so,createdump} "$appdir"/usr/lib/thetis/*.pdb

install -m 755 "$here/packaging/AppRun" "$appdir/AppRun"
cp "$here/packaging/thetis.png" "$appdir/thetis.png"
cp "$here/packaging/thetis.png" "$appdir/usr/share/icons/hicolor/256x256/apps/thetis.png"
ln -s thetis.png "$appdir/.DirIcon"
sed -e 's|^Exec=.*|Exec=thetis|' -e 's|^Icon=.*|Icon=thetis|' \
    "$here/packaging/thetis.desktop.in" > "$appdir/thetis.desktop"
echo "X-AppImage-Version=$version" >> "$appdir/thetis.desktop"
cp "$appdir/thetis.desktop" "$appdir/usr/share/applications/thetis.desktop"
cp "$here/packaging/thetis.appdata.xml" "$appdir/usr/share/metainfo/thetis.appdata.xml"

# --- appimagetool -------------------------------------------------------------
runtime_args=()
tool="${APPIMAGETOOL:-$(command -v appimagetool || true)}"
if [ -z "$tool" ]; then
    mkdir -p "$tools"
    base=https://github.com/AppImage
    if [ ! -x "$tools/squashfs-root/AppRun" ]; then
        curl -fsSL -o "$tools/appimagetool.AppImage" "$base/appimagetool/releases/download/continuous/appimagetool-$arch.AppImage"
        chmod +x "$tools/appimagetool.AppImage"
        # extract instead of running it, so FUSE is not needed to build
        (cd "$tools" && ./appimagetool.AppImage --appimage-extract >/dev/null)
    fi
    [ -f "$tools/runtime-$arch" ] || \
        curl -fsSL -o "$tools/runtime-$arch" "$base/type2-runtime/releases/download/continuous/runtime-$arch"
    tool="$tools/squashfs-root/AppRun"
    runtime_args=(--runtime-file "$tools/runtime-$arch")
fi

mkdir -p "$out"
target="$out/Thetis-$version-$arch.AppImage"
ARCH=$arch "$tool" --no-appstream --comp zstd --mksquashfs-opt -Xcompression-level --mksquashfs-opt 22 --mksquashfs-opt -b --mksquashfs-opt 1M "${runtime_args[@]}" "$appdir" "$target"
chmod +x "$target"
echo "AppImage: $target"
