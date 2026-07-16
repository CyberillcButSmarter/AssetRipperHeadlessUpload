# Deploying the AssetRipper headless host on Linux

This directory turns a Linux box into an always-on AssetRipper host that you can
upload games to from another machine, and that **auto-updates itself** whenever a
new `continuous` release is published by the GitHub Actions build.

## What gets installed

| File | Purpose |
|------|---------|
| `assetripper.service` | Runs the host as user `assetripper`, bound per `/etc/assetripper.env`. |
| `update-assetripper.sh` | Checks the `continuous` release; downloads + swaps in a newer build and restarts the service. |
| `assetripper-update.service` + `.timer` | Runs the updater every ~15 minutes. |
| `install.sh` | One-time setup: user, files, units, first download. |

## Install

```bash
git clone https://github.com/CyberillcButSmarter/AssetRipperHeadlessUpload.git
cd AssetRipperHeadlessUpload
sudo ./deploy/install.sh
```

That installs everything, downloads the current build, starts the service on
`0.0.0.0:8080`, and enables the 15-minute update timer.

Open `http://<box-ip>:8080/Commands` from any machine on your LAN/VPN, upload a
game (an apk/exe, or a **zipped** data folder), and download the decompiled result
with the "Export … (.zip)" buttons.

## Configure

Edit `/etc/assetripper.env` then `sudo systemctl restart assetripper`:

```ini
HOST=0.0.0.0      # 127.0.0.1 to restrict to localhost / a reverse proxy
PORT=8080
REPO=CyberillcButSmarter/AssetRipperHeadlessUpload
TAG=continuous
# GITHUB_TOKEN=   # only for private repos or higher API rate limits
```

## Operate

```bash
systemctl status assetripper              # is the host running?
journalctl -u assetripper -f              # live host logs
systemctl start assetripper-update        # force an update check now
journalctl -u assetripper-update -e       # last update run
systemctl list-timers assetripper-update  # when the next check fires
```

## How the auto-update stays safe

The updater is built so a bad or interrupted download can never brick the host:

- **Change detection** — compares the release's `published_at` (which changes on
  every rebuild) against `/opt/assetripper/.release_stamp`; an unchanged release
  is a no-op.
- **Single-instance lock** — `flock` prevents two update runs (e.g. an overlapping
  timer tick) from racing.
- **Verify before touching anything live** — the download's size is checked against
  the size the GitHub API reports, `xz -t` tests the archive for truncation/
  corruption, and the executable's presence is confirmed after extract. Any failure
  aborts while the running install is still untouched.
- **Atomic activation** — each build is extracted to `releases/<stamp>/` and made
  live by flipping the `current` symlink with an atomic rename, so the service never
  runs against a half-written directory.
- **Health check + rollback** — after restart, if the service doesn't stay up, the
  `current` symlink is flipped back to the previous release and the service
  restarted.
- **Commit-on-success** — the version stamp is written only after a confirmed-healthy
  start, so a failed update just retries on the next tick. The last few releases are
  kept under `releases/` for rollback (tune with `KEEP` in the env file).

Layout on disk:

```
/opt/assetripper/
├── current -> releases/<stamp>   # atomic symlink the service runs from
├── releases/<stamp>/             # each downloaded build
├── update-assetripper.sh
└── .release_stamp                # installed version marker
```

## Security notes

- The host runs a decompiler over whatever you upload, so keep it **LAN/VPN-only**
  (the default `0.0.0.0` bind + a firewall, or bind `127.0.0.1` behind a reverse
  proxy with auth). Don't expose port 8080 to the internet.
- The service runs as the unprivileged `assetripper` user; only the updater needs root.
