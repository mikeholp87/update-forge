#!/usr/bin/env bash
# Publishes UpdateForge (GUI + CLI) self-contained for Linux and creates a portable tar.gz and a .deb.
#   ./scripts/publish-linux.sh [version] [linux-x64|linux-arm64]
set -euo pipefail

version="${1:-0.2.0}"
rid="${2:-linux-x64}"
case "$rid" in
linux-x64) arch=amd64 ;;
linux-arm64) arch=arm64 ;;
*)
	echo "unsupported runtime: $rid" >&2
	exit 2
	;;
esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
out="artifacts/publish/$rid"
pkg="artifacts/deb/updateforge_${version}_${arch}"
rm -rf "$out" "$pkg"
mkdir -p "$out" artifacts

# Both executables go to the same folder: the systemd timer runs updateforge-cli from next to updateforge.
for proj in src/UpdateForge.Desktop.Avalonia src/UpdateForge.Cli; do
	dotnet publish "$proj" -c Release -f net10.0 -r "$rid" --self-contained true -o "$out" \
		"-p:Version=$version" -p:DebugType=none -p:DebugSymbols=false
done
cp LICENSE "$out/"

# Portable tarball (unpack anywhere, run ./updateforge).
tar -C "$out" -czf "artifacts/UpdateForge-$version-$rid-portable.tar.gz" .

# .deb: files under /opt/updateforge, launchers in /usr/bin, desktop entry + icon.
install -d "$pkg/DEBIAN" "$pkg/opt/updateforge" "$pkg/usr/bin" "$pkg/usr/share/applications" "$pkg/usr/share/icons/hicolor/512x512/apps"
cp -a "$out/." "$pkg/opt/updateforge/"
ln -s /opt/updateforge/updateforge "$pkg/usr/bin/updateforge"
ln -s /opt/updateforge/updateforge-cli "$pkg/usr/bin/updateforge-cli"
install -m 644 packaging/linux/updateforge.desktop "$pkg/usr/share/applications/updateforge.desktop"
install -m 644 src/UpdateForge.Desktop/Assets/UpdateForge-512.png "$pkg/usr/share/icons/hicolor/512x512/apps/updateforge.png"
cat >"$pkg/DEBIAN/control" <<CONTROL
Package: updateforge
Version: $version
Architecture: $arch
Maintainer: Mike Holp <4627227+mikeholp87@users.noreply.github.com>
Section: utils
Priority: optional
Homepage: https://mikeholp87.github.io/update-forge/
Depends: libc6, libstdc++6, zlib1g, libfontconfig1, libx11-6, libice6, libsm6, libicu72 | libicu74 | libicu76 | libicu77 | libicu78
Recommends: policykit-1 | polkitd, libnotify-bin, network-manager
Description: Software updater for apt, Flatpak, Snap and npm
 UpdateForge inventories what is installed, shows which applications have a
 newer version, and installs updates through the system's package managers,
 on demand or on a schedule (systemd user timer). No telemetry, no accounts.
CONTROL
dpkg-deb --root-owner-group --build "$pkg" "artifacts/updateforge_${version}_${arch}.deb" >/dev/null

(cd artifacts && sha256sum "UpdateForge-$version-$rid-portable.tar.gz" "updateforge_${version}_${arch}.deb" >"SHA256SUMS-linux")
echo "Wrote artifacts/UpdateForge-$version-$rid-portable.tar.gz and artifacts/updateforge_${version}_${arch}.deb"
