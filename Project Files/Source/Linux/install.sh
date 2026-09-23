#!/usr/bin/env bash
# Install the application built by './build.sh --app' for the current user:
#   ~/.local/opt/thetis            program files
#   ~/.local/bin/thetis            launcher
#   ~/.local/share/applications    menu entry (appears in the Linux Mint menu)
#
#   ./install.sh             install / update
#   ./install.sh --uninstall remove (settings in ~/.config/thetis-linux are kept)
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dist_dir="${DIST_DIR:-$here/dist/thetis}"
prefix="$HOME/.local"
opt="$prefix/opt/thetis"
desktop="$prefix/share/applications/thetis.desktop"

if [ "${1:-}" = "--uninstall" ]; then
  rm -rf "$opt" "$prefix/bin/thetis" "$desktop"
  echo "Thetis removed (settings in ~/.config/thetis-linux were kept)."
  exit 0
fi

if [ ! -x "$dist_dir/thetis" ]; then
  echo "No application in $dist_dir - run ./build.sh --app first." >&2
  exit 1
fi

mkdir -p "$opt" "$prefix/bin" "$(dirname "$desktop")"
rm -rf "$opt"/*
cp -a "$dist_dir"/. "$opt"/
ln -sf "$opt/thetis" "$prefix/bin/thetis"
sed "s|@OPT@|$opt|g" "$here/packaging/thetis.desktop.in" > "$desktop"
update-desktop-database "$(dirname "$desktop")" 2>/dev/null || true

echo "Installed to $opt"
echo "Start it from the menu (Thetis) or run: thetis"
if ! id -nG | grep -qw audio; then
  me="${USER:-$(id -un)}"
  echo
  echo "Tip: for real-time audio priority run"
  echo "  sudo usermod -aG audio $me"
  echo "and see README.md (Real-time priority)."
fi
