#!/usr/bin/env bash
# Build Thetis for Linux Mint / Ubuntu / Debian.
#
#   ./build.sh --deps     first install the build dependencies with apt
#   ./build.sh            build and test the native libraries into ./build
#   ./build.sh --app      also publish the Avalonia application into ./dist/thetis
#   ./build.sh --appimage also package it as ./dist/Thetis-<version>-x86_64.AppImage
#   ./build.sh --native   optimise the native libraries for this CPU (-march=native)
#
# Native libraries: ./build/lib{wdsp,ChannelMaster,PA19}.so
# Application:      ./dist/thetis/thetis  (self-contained; no .NET install needed to run it)
# Install it for your user with ./install.sh, or run the AppImage directly
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
build_dir="${BUILD_DIR:-$here/build}"
dist_dir="${DIST_DIR:-$here/dist/thetis}"
native=OFF
app=0
appimage=0

for arg in "$@"; do
  case "$arg" in
    --deps)
      sudo apt-get update
      sudo apt-get install -y build-essential cmake pkg-config \
        libfftw3-dev zlib1g-dev libasound2-dev libpulse-dev libjack-jackd2-dev git \
        dotnet-sdk-8.0
      ;;
    --native) native=ON ;;
    --app) app=1 ;;
    --appimage) app=1; appimage=1 ;;
    -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

cmake -S "$here" -B "$build_dir" -DCMAKE_BUILD_TYPE=Release -DTHETIS_NATIVE_ARCH="$native"
cmake --build "$build_dir" -j"$(nproc)"
(cd "$build_dir" && ctest --output-on-failure)

echo
echo "Native libraries:"
ls -l "$build_dir"/lib{wdsp,WDSP,ChannelMaster,PA19,aethernr}.so

if [ "$app" = 1 ]; then
  echo
  echo "Publishing the application..."
  rm -rf "$dist_dir"
  dotnet publish "$here/Thetis.Desktop/Thetis.Desktop.csproj" -c Release \
    -r linux-x64 --self-contained true -o "$dist_dir" \
    -p:SourceRevisionId="$(git -C "$here" rev-parse --short HEAD 2>/dev/null || echo local)"
  cp -a "$build_dir"/libwdsp.so "$build_dir"/libWDSP.so "$build_dir"/libChannelMaster.so "$build_dir"/libPA19.so "$dist_dir"/
  # AetherSDR noise reduction, and DFNR's model if it was built in
  cp -a "$build_dir"/libaethernr.so "$dist_dir"/
  [ -f "$build_dir"/DeepFilterNet3_onnx.tar.gz ] && cp -a "$build_dir"/DeepFilterNet3_onnx.tar.gz "$dist_dir"/
  cp -a "$here/packaging/thetis.png" "$dist_dir"/ 2>/dev/null || true
  echo "Application: $dist_dir/thetis"
fi

if [ "$appimage" = 1 ]; then
  echo
  echo "Packaging the AppImage..."
  "$here/packaging/build-appimage.sh" "$dist_dir" "$(dirname "$dist_dir")"
fi
