#!/usr/bin/env bash
# Build the Thetis native libraries (wdsp, ChannelMaster, PA19) on Linux Mint /
# Ubuntu / Debian.
#
#   ./build.sh            configure, build and test into ./build
#   ./build.sh --deps     first install the build dependencies with apt
#   ./build.sh --native   optimise for this machine's CPU (-march=native)
#
# The libraries end up in ./build (libwdsp.so, libChannelMaster.so, libPA19.so).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
build_dir="${BUILD_DIR:-$here/build}"
native=OFF

for arg in "$@"; do
  case "$arg" in
    --deps)
      sudo apt-get update
      sudo apt-get install -y build-essential cmake pkg-config \
        libfftw3-dev libasound2-dev libpulse-dev libjack-jackd2-dev
      ;;
    --native) native=ON ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

cmake -S "$here" -B "$build_dir" -DCMAKE_BUILD_TYPE=Release -DTHETIS_NATIVE_ARCH="$native"
cmake --build "$build_dir" -j"$(nproc)"
(cd "$build_dir" && ctest --output-on-failure)

echo
echo "Built:"
ls -l "$build_dir"/lib{wdsp,WDSP,ChannelMaster,PA19}.so
