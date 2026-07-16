#!/usr/bin/env bash
# One-time installer for the AssetRipper headless host on a Linux box.
# Sets up a dedicated user, the systemd service, and the auto-update timer,
# then pulls the first build. Run as root:  sudo ./deploy/install.sh
set -euo pipefail

[ "$(id -u)" -eq 0 ] || { echo "Please run as root (sudo ./install.sh)." >&2; exit 1; }

ROOT=/opt/assetripper
SRC_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "==> Installing dependencies (jq, curl, xz, rsync) ..."
if command -v apt-get >/dev/null 2>&1; then
	apt-get update -y && apt-get install -y jq curl xz-utils rsync
elif command -v dnf >/dev/null 2>&1; then
	dnf install -y jq curl xz rsync
else
	echo "!! Unknown package manager - ensure jq, curl, xz and rsync are installed." >&2
fi

echo "==> Creating service user 'assetripper' ..."
id assetripper >/dev/null 2>&1 || useradd --system --home "$ROOT" --shell /usr/sbin/nologin assetripper

echo "==> Laying down files under $ROOT ..."
mkdir -p "$ROOT/releases"   # the updater fills this and manages the 'current' symlink
install -m 0755 "$SRC_DIR/update-assetripper.sh" "$ROOT/update-assetripper.sh"

if [ ! -f /etc/assetripper.env ]; then
	echo "==> Writing default /etc/assetripper.env ..."
	cat > /etc/assetripper.env <<'EOF'
# Bind address and port for the host. 0.0.0.0 = reachable on your LAN/VPN.
# 8080 is avoided by default because Docker and many apps grab it; change freely.
HOST=0.0.0.0
PORT=8087
# Release source for the auto-updater.
REPO=CyberillcButSmarter/AssetRipperHeadlessUpload
TAG=continuous
# Optional: a GitHub token (only needed for private repos or higher API rate limits).
# GITHUB_TOKEN=
EOF
fi

echo "==> Installing systemd units ..."
install -m 0644 "$SRC_DIR/assetripper.service"        /etc/systemd/system/assetripper.service
install -m 0644 "$SRC_DIR/assetripper-update.service" /etc/systemd/system/assetripper-update.service
install -m 0644 "$SRC_DIR/assetripper-update.timer"   /etc/systemd/system/assetripper-update.timer

chown -R assetripper:assetripper "$ROOT"
systemctl daemon-reload

echo "==> Pulling the first build ..."
/opt/assetripper/update-assetripper.sh

echo "==> Enabling service + update timer ..."
systemctl enable --now assetripper.service
systemctl enable --now assetripper-update.timer

echo
echo "Done. The host is running and will auto-update every ~15 minutes."
systemctl --no-pager --lines=0 status assetripper.service || true
