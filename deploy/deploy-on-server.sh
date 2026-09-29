#!/usr/bin/env bash
#
# deploy-on-server.sh: unpack a build tarball and swap it into
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

# A self-contained build carries the .NET runtime (only it ships libcoreclr.so). Otherwise
# check for the ASP.NET Core 10 runtime before anything is swapped.
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
    # The apps write logs/ next to their assembly. Carry it across, or the next deploy's
    # .old cleanup deletes it.
    if [[ -d "$APP_HOME/$name.old/logs" && ! -e "$APP_HOME/$name/logs" ]]; then
        mv "$APP_HOME/$name.old/logs" "$APP_HOME/$name/logs"
    fi
}

# Pause the sync timer so no run starts against a half-installed build. It restarts at the
# end only if it was running.
timer_was_active=0
systemctl is-active --quiet mtcalsync-sync.timer && timer_was_active=1
systemctl stop mtcalsync-sync.timer 2>/dev/null || true
# Let a run already in progress finish (up to the ten-minute sync lease) before its files move.
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

# Set ownership and modes here, never from the bundle: root's tar keeps recorded modes, and a
# Windows-built bundle records 0666/0777, which would make the code world-writable. The service
# account owns only its publish dirs (it writes logs there); the rest is root's, because root
# runs the scripts under deploy/.
chown root:root "$APP_HOME"
chmod 0755 "$APP_HOME"
for d in worker-publish selfhost-publish worker-publish.old selfhost-publish.old; do
    [[ -d "$APP_HOME/$d" ]] || continue
    chown -R mtcalsync:mtcalsync "$APP_HOME/$d"
    find "$APP_HOME/$d" -type d -exec chmod 0750 {} +
    find "$APP_HOME/$d" -type f -exec chmod 0640 {} +
done
# The units run each app's native launcher, which needs the execute bit a Windows-built
# bundle doesn't record.
for app in worker-publish/Worker.MT-CalSync selfhost-publish/SelfHost.MT-CalSync \
           worker-publish.old/Worker.MT-CalSync selfhost-publish.old/SelfHost.MT-CalSync; do
    if [[ -f "$APP_HOME/$app" ]]; then chmod 0750 "$APP_HOME/$app"; fi
done
chown -R root:root "$APP_HOME/deploy" "$APP_HOME/scripts"
find "$APP_HOME/deploy" "$APP_HOME/scripts" -type d -exec chmod 0755 {} +
find "$APP_HOME/deploy" "$APP_HOME/scripts" -type f -exec chmod 0644 {} +

# Install the units on every deploy so an existing install picks up changes (such as a
# new ExecStart).
for unit in mtcalsync-worker@.service mtcalsync-sync.timer mtcalsync-selfhost.service; do
    if [[ -f "$APP_HOME/deploy/$unit" ]]; then install -m0644 "$APP_HOME/deploy/$unit" /etc/systemd/system/; fi
done
systemctl daemon-reload

systemctl start mtcalsync-selfhost.service
if [[ $timer_was_active -eq 1 ]]; then systemctl start mtcalsync-sync.timer; fi
echo "Deployed. worker + self-host portal swapped; previous kept as *.old."
