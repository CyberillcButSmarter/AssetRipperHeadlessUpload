#!/usr/bin/env bash
# Safely updates the AssetRipper host to the latest "continuous" release.
#
# Safety design:
#   * flock ensures only one update runs at a time.
#   * The download is verified (size vs the GitHub API + `xz -t` integrity)
#     BEFORE anything live is touched, so a truncated/corrupt download aborts
#     with the running install untouched.
#   * New builds are extracted into releases/<stamp>/ and activated by an atomic
#     symlink flip (current -> that dir), so the service never sees a half-written
#     directory.
#   * After restart the service is health-checked; if it fails to come up the
#     symlink is rolled back to the previous release and the service restarted.
#   * The installed-version stamp is written only after a confirmed-healthy start,
#     so a failed update simply retries on the next timer tick.
#
# Intended to run as root (via assetripper-update.timer), or manually.
set -euo pipefail

# Defaults (overridable via /etc/assetripper.env)
ROOT="${ROOT:-/opt/assetripper}"
REPO="${REPO:-CyberillcButSmarter/AssetRipperHeadlessUpload}"
TAG="${TAG:-continuous}"
SERVICE="${SERVICE:-assetripper}"
RUN_AS="${RUN_AS:-assetripper}"
KEEP="${KEEP:-3}"            # old releases to retain for rollback
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-20}"  # seconds to confirm the service stays up

# shellcheck disable=SC1091
[ -f /etc/assetripper.env ] && . /etc/assetripper.env

RELEASES_DIR="$ROOT/releases"
CURRENT_LINK="$ROOT/current"
STAMP_FILE="$ROOT/.release_stamp"
LOCK_FILE="$ROOT/.update.lock"

log() { echo "[assetripper-update] $*"; }

mkdir -p "$ROOT"

# --- single-instance lock -------------------------------------------------
exec 9>"$LOCK_FILE"
if ! flock -n 9; then
	log "Another update run holds the lock; exiting."
	exit 0
fi

# --- pick the right asset for this box ------------------------------------
case "$(uname -m)" in
	x86_64)        ASSET="AssetRipper_linux_x64.tar.xz" ;;
	aarch64|arm64) ASSET="AssetRipper_linux_arm64.tar.xz" ;;
	*) log "Unsupported architecture: $(uname -m)"; exit 1 ;;
esac

api="https://api.github.com/repos/$REPO/releases/tags/$TAG"
auth=()
[ -n "${GITHUB_TOKEN:-}" ] && auth=(-H "Authorization: Bearer $GITHUB_TOKEN")

log "Checking $REPO release '$TAG' for $ASSET ..."
json="$(curl -fsSL --retry 3 --retry-delay 2 -H 'Accept: application/vnd.github+json' "${auth[@]}" "$api")"

# published_at changes on every rebuild (the release is recreated each time).
remote_stamp="$(printf '%s' "$json" | jq -r '.published_at // .created_at // empty')"
[ -n "$remote_stamp" ] || { log "Could not read release timestamp."; exit 1; }

local_stamp="$(cat "$STAMP_FILE" 2>/dev/null || true)"
if [ "$remote_stamp" = "$local_stamp" ] && [ -x "$CURRENT_LINK/AssetRipper.GUI.Free" ]; then
	log "Already up to date ($remote_stamp)."
	exit 0
fi

url="$(printf '%s' "$json" | jq -r --arg A "$ASSET" '.assets[] | select(.name==$A) | .browser_download_url')"
expected_size="$(printf '%s' "$json" | jq -r --arg A "$ASSET" '.assets[] | select(.name==$A) | .size')"
[ -n "$url" ] && [ "$url" != "null" ] || { log "Release asset '$ASSET' not found in '$TAG'."; exit 1; }

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
archive="$tmp/app.tar.xz"

log "Downloading new build ($remote_stamp) ..."
curl -fL --retry 5 --retry-delay 3 -o "$archive" "$url"

# --- integrity checks (before touching anything live) ---------------------
if [ -n "$expected_size" ] && [ "$expected_size" != "null" ]; then
	actual_size="$(stat -c%s "$archive" 2>/dev/null || wc -c < "$archive")"
	if [ "$actual_size" != "$expected_size" ]; then
		log "Size mismatch: got $actual_size, expected $expected_size - aborting."
		exit 1
	fi
fi
if ! xz -t "$archive" 2>/dev/null; then
	log "Downloaded archive failed integrity check (truncated/corrupt) - aborting."
	exit 1
fi

stage="$tmp/extract"
mkdir -p "$stage"
tar -xJf "$archive" -C "$stage"
[ -f "$stage/AssetRipper.GUI.Free" ] || { log "Extracted build is missing the executable - aborting."; exit 1; }
chmod +x "$stage/AssetRipper.GUI.Free"

# --- stage into a versioned release dir -----------------------------------
mkdir -p "$RELEASES_DIR"
safe_stamp="$(printf '%s' "$remote_stamp" | tr -c 'A-Za-z0-9._-' '_')"
new_release="$RELEASES_DIR/$safe_stamp"
rm -rf "$new_release"
mv "$stage" "$new_release"
id "$RUN_AS" >/dev/null 2>&1 && chown -R "$RUN_AS":"$RUN_AS" "$new_release"

# Record the currently-active release for rollback. Only if 'current' is a real
# symlink (not on first install) - resolving a non-existent link would otherwise
# yield the link's own path and cause a self-referential rollback.
previous_target=""
if [ -L "$CURRENT_LINK" ]; then
	previous_target="$(readlink -f "$CURRENT_LINK" 2>/dev/null || true)"
fi

# --- atomic activation ----------------------------------------------------
ln -sfn "$new_release" "$CURRENT_LINK.tmp"
mv -Tf "$CURRENT_LINK.tmp" "$CURRENT_LINK"

log "Restarting $SERVICE ..."
systemctl restart "$SERVICE" || true

# --- health check: wait for it to come up, then confirm it stays up -------
healthy=1
for _ in $(seq 1 "$HEALTH_TIMEOUT"); do
	if systemctl is-active --quiet "$SERVICE"; then
		healthy=0
		break
	fi
	sleep 1
done
# Guard against a crash-loop: confirm it is still active a few seconds later.
if [ "$healthy" -eq 0 ]; then
	sleep 3
	systemctl is-active --quiet "$SERVICE" || healthy=1
fi

if [ "$healthy" -ne 0 ]; then
	log "New build did not stay healthy after restart. Recent service logs:"
	journalctl -u "$SERVICE" -n 40 --no-pager 2>/dev/null | sed 's/^/    /' || true
	if [ -n "$previous_target" ] && [ -d "$previous_target" ] && [ "$previous_target" != "$new_release" ]; then
		log "Rolling back to previous release: $previous_target"
		ln -sfn "$previous_target" "$CURRENT_LINK.tmp"
		mv -Tf "$CURRENT_LINK.tmp" "$CURRENT_LINK"
		systemctl restart "$SERVICE" || true
	else
		log "No previous healthy release to roll back to (leaving new build in place for diagnosis)."
	fi
	exit 1
fi

# --- commit the stamp only after a confirmed-healthy start ----------------
printf '%s\n' "$remote_stamp" > "$STAMP_FILE"
log "Updated to $remote_stamp and confirmed healthy."

# --- prune old releases (keep newest $KEEP; never remove current/previous) -
if [ -d "$RELEASES_DIR" ]; then
	# shellcheck disable=SC2012
	ls -1dt "$RELEASES_DIR"/*/ 2>/dev/null | tail -n +"$((KEEP + 1))" | while read -r old; do
		old="${old%/}"
		[ "$old" = "$new_release" ] && continue
		[ "$old" = "$previous_target" ] && continue
		log "Pruning old release: $old"
		rm -rf "$old"
	done
fi
