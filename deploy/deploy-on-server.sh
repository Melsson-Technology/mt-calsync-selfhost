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
    # The worker and portal write logs/ next to their assembly. Left behind in .old, the
    # log history was deleted by the deploy after next, so carry it across.
    if [[ -d "$APP_HOME/$name.old/logs" && ! -e "$APP_HOME/$name/logs" ]]; then
        mv "$APP_HOME/$name.old/logs" "$APP_HOME/$name/logs"
    fi
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

# Ownership and modes are set here, never taken from the bundle. Root's tar keeps the
# modes a bundle recorded, and a bundle built on Windows records every file as 0666 and
# every directory as 0777: world-writable code, run by the account that can read the
# encryption key. The service account owns only its publish dirs, which it writes logs
# into. Everything else stays root's, because root runs the scripts kept under deploy/.
chown root:root "$APP_HOME"
chmod 0755 "$APP_HOME"
for d in worker-publish selfhost-publish worker-publish.old selfhost-publish.old; do
    [[ -d "$APP_HOME/$d" ]] || continue
    chown -R mtcalsync:mtcalsync "$APP_HOME/$d"
    find "$APP_HOME/$d" -type d -exec chmod 0750 {} +
    find "$APP_HOME/$d" -type f -exec chmod 0640 {} +
done
chown -R root:root "$APP_HOME/deploy" "$APP_HOME/scripts"
find "$APP_HOME/deploy" "$APP_HOME/scripts" -type d -exec chmod 0755 {} +
find "$APP_HOME/deploy" "$APP_HOME/scripts" -type f -exec chmod 0644 {} +

systemctl start mtcalsync-selfhost.service
echo "Deployed. worker + self-host portal swapped; previous kept as *.old."
