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

# A self-contained build carries the .NET runtime (libcoreclr.so ships only with one). A
# framework-dependent build needs the ASP.NET Core 10 runtime installed, so check for it
# before anything is swapped, not after the portal fails to start.
if [[ -f "$STAGE/selfhost-publish/libcoreclr.so" ]]; then
    echo "==> self-contained build: no .NET runtime needed on this server"
elif ! dotnet --list-runtimes 2>/dev/null | grep -q "Microsoft.AspNetCore.App 10\."; then
    echo "ERROR: this build needs the ASP.NET Core 10 runtime, and it isn't installed." >&2
    echo "       Install it (_docs/INSTALL.md, step 1), or build self-contained instead:" >&2
    echo "       ./scripts/build-and-package.sh --self-contained" >&2
    exit 1
fi

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

# The sync timer is paused for the swap, so no run starts against a half-installed build or
# a unit that doesn't match it yet. It is restarted at the end only if it was running.
timer_was_active=0
systemctl is-active --quiet mtcalsync-sync.timer && timer_was_active=1
systemctl stop mtcalsync-sync.timer 2>/dev/null || true
# A run the timer started just before is still going; let it finish (up to ten minutes, the
# sync lease) before its files move.
for _ in $(seq 1 600); do
    case "$(systemctl show -p ActiveState --value mtcalsync-worker@sync.service 2>/dev/null)" in
        activating|active|deactivating|reloading) sleep 1 ;;
        *) break ;;
    esac
done
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
# The units run each app's native launcher, which must stay executable. A bundle made on
# Windows records no execute bit at all.
for app in worker-publish/Worker.MT-CalSync selfhost-publish/SelfHost.MT-CalSync \
           worker-publish.old/Worker.MT-CalSync selfhost-publish.old/SelfHost.MT-CalSync; do
    [[ -f "$APP_HOME/$app" ]] && chmod 0750 "$APP_HOME/$app"
done
chown -R root:root "$APP_HOME/deploy" "$APP_HOME/scripts"
find "$APP_HOME/deploy" "$APP_HOME/scripts" -type d -exec chmod 0755 {} +
find "$APP_HOME/deploy" "$APP_HOME/scripts" -type f -exec chmod 0644 {} +

# The units come from the bundle on every deploy, so an existing install picks up a changed
# unit (such as a new ExecStart) instead of keeping the one it was provisioned with.
for unit in mtcalsync-worker@.service mtcalsync-sync.timer mtcalsync-selfhost.service; do
    [[ -f "$APP_HOME/deploy/$unit" ]] && install -m0644 "$APP_HOME/deploy/$unit" /etc/systemd/system/
done
systemctl daemon-reload

systemctl start mtcalsync-selfhost.service
[[ $timer_was_active -eq 1 ]] && systemctl start mtcalsync-sync.timer
echo "Deployed. worker + self-host portal swapped; previous kept as *.old."
