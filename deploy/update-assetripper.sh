#!/usr/bin/env bash
# Downloads the latest "continuous" release for this Linux box and, if it is newer
# than what is installed, swaps it in and restarts the service.
# Intended to be run as root by assetripper-update.timer (or manually).
set -euo pipefail

# Defaults (overridable via /etc/assetripper.env)
ROOT="${ROOT:-/opt/assetripper}"
REPO="${REPO:-CyberillcButSmarter/AssetRipperHeadlessUpload}"
TAG="${TAG:-continuous}"
SERVICE="${SERVICE:-assetripper}"
RUN_AS="${RUN_AS:-assetripper}"

# shellcheck disable=SC1091
[ -f /etc/assetripper.env ] && . /etc/assetripper.env

APP_DIR="$ROOT/app"
STAMP_FILE="$ROOT/.release_stamp"

case "$(uname -m)" in
	x86_64)        ASSET="AssetRipper_linux_x64.tar.xz" ;;
	aarch64|arm64) ASSET="AssetRipper_linux_arm64.tar.xz" ;;
	*) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

api="https://api.github.com/repos/$REPO/releases/tags/$TAG"
auth=()
[ -n "${GITHUB_TOKEN:-}" ] && auth=(-H "Authorization: Bearer $GITHUB_TOKEN")

echo "Checking $REPO release '$TAG' for $ASSET ..."
json="$(curl -fsSL -H 'Accept: application/vnd.github+json' "${auth[@]}" "$api")"

# The release is deleted+recreated on every build, so published_at changes each time.
remote_stamp="$(printf '%s' "$json" | jq -r '.published_at // .created_at // empty')"
[ -n "$remote_stamp" ] || { echo "Could not read release timestamp." >&2; exit 1; }

local_stamp="$(cat "$STAMP_FILE" 2>/dev/null || true)"
if [ "$remote_stamp" = "$local_stamp" ]; then
	echo "Already up to date ($remote_stamp)."
	exit 0
fi

url="$(printf '%s' "$json" | jq -r --arg A "$ASSET" '.assets[] | select(.name==$A) | .browser_download_url')"
[ -n "$url" ] || { echo "Release asset '$ASSET' not found in '$TAG'." >&2; exit 1; }

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Downloading new build ($remote_stamp) ..."
curl -fL "$url" -o "$tmp/app.tar.xz"   # public asset: no auth header (avoids redirect signature issues)
mkdir -p "$tmp/extract"
tar -xJf "$tmp/app.tar.xz" -C "$tmp/extract"

echo "Installing update ..."
systemctl stop "$SERVICE" 2>/dev/null || true
mkdir -p "$APP_DIR"
if command -v rsync >/dev/null 2>&1; then
	rsync -a --delete "$tmp/extract/" "$APP_DIR/"
else
	rm -rf "${APP_DIR:?}/"* && cp -a "$tmp/extract/." "$APP_DIR/"
fi
chmod +x "$APP_DIR/AssetRipper.GUI.Free"
id "$RUN_AS" >/dev/null 2>&1 && chown -R "$RUN_AS":"$RUN_AS" "$APP_DIR"
printf '%s\n' "$remote_stamp" > "$STAMP_FILE"

systemctl start "$SERVICE"
echo "Updated to $remote_stamp and restarted '$SERVICE'."
