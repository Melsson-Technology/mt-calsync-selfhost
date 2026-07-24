#!/usr/bin/env bash
#
# deploy-on-server.sh — unpack a build tarball and atomically swap it into
# /opt/mtcalsync. Copy mtcalsync-engine.tar.gz to the server, then run as root:
#
#   sudo ./deploy-on-server.sh mtcalsync-engine.tar.gz
#
# The previous publish dirs are kept as *.old for a quick rollback.

set -euo pipefail

TARBALL="${1:?usage: deploy-on-server.sh <tarball>}"
APP_HOME="/opt/mtcalsync"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

tar -xzf "$TARBALL" -C "$STAGE"

swap() {   # swap <publish-dir-name>
    local name="$1"
    [[ -d "$STAGE/$name" ]] || return 0
    if [[ -d "$APP_HOME/$name" ]]; then rm -rf "$APP_HOME/$name.old"; mv "$APP_HOME/$name" "$APP_HOME/$name.old"; fi
    mv "$STAGE/$name" "$APP_HOME/$name"
}

systemctl stop mtcalsync-selfhost.service 2>/dev/null || true
swap worker-publish
swap selfhost-publish

mkdir -p "$APP_HOME/scripts" "$APP_HOME/deploy"
cp -r "$STAGE/scripts/." "$APP_HOME/scripts/" 2>/dev/null || true
cp -r "$STAGE/deploy/."  "$APP_HOME/deploy/"  2>/dev/null || true

# settings.xml lives in /etc/mtcalsync and is symlinked into each publish dir.
ln -sf /etc/mtcalsync/settings.xml "$APP_HOME/worker-publish/settings.xml"
ln -sf /etc/mtcalsync/settings.xml "$APP_HOME/selfhost-publish/settings.xml"

chown -R mtcalsync:mtcalsync "$APP_HOME"
systemctl start mtcalsync-selfhost.service
echo "Deployed. worker + self-host portal swapped; previous kept as *.old."
