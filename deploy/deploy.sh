#!/usr/bin/env bash
# Uploads a release archive to the server and installs it with synect-release (migration, restart of Worker and Api,
# /ready check, switch of /opt/synect/current; automatic switch back when /ready fails).
#
#   deploy/deploy.sh root@2.29.216.38 [artifacts/synect-....tar.gz]   (default: the newest archive in artifacts/)
#   deploy/deploy.sh root@2.29.216.38 --scripts-only                   only refreshes the server scripts
#
# Rollback: ssh root@<server> synect-release rollback
set -euo pipefail

target="${1:?usage: deploy.sh user@host [archive.tar.gz|--scripts-only]}"
repo="$(cd "$(dirname "$0")/.." && pwd)"

# Server-side scripts and units travel with every deployment, so the server always runs the reviewed versions.
scp -q "$repo/deploy/server/synect-release" "$repo/deploy/server/synect-devtools" "$repo/deploy/server/synect-backup" \
  "$repo/deploy/server/synect-restore-test" "$target:/usr/local/sbin/"
ssh "$target" 'chmod 755 /usr/local/sbin/synect-release /usr/local/sbin/synect-devtools /usr/local/sbin/synect-backup /usr/local/sbin/synect-restore-test'
[[ "${2:-}" == "--scripts-only" ]] && { echo "Scripts updated."; exit 0; }

archive="${2:-$(ls -t "$repo"/artifacts/synect-*.tar.gz 2>/dev/null | head -1)}"
[[ -f "$archive" ]] || { echo "No archive: run deploy/build.sh first." >&2; exit 1; }

remote="/var/tmp/$(basename "$archive")"
echo "== upload $(basename "$archive")"
scp -q "$archive" "$target:$remote"
echo "== install"
# The uploaded archive stays in /var/tmp (systemd-tmpfiles cleans /var/tmp after 30 days).
ssh "$target" "synect-release deploy '$remote'"
