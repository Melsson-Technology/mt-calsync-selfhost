#!/usr/bin/env bash
#
# build-and-package.sh: publish the MT-CalSync engine (worker + self-host portal) for
# linux-x64 and bundle the schema + deploy assets into a tarball.
#
# Run from anywhere:  ./scripts/build-and-package.sh [--self-contained]
# Produces:           build/mtcalsync-engine.tar.gz
#
# Copy the tarball to your server and run deploy/deploy-on-server.sh there.
#
# Framework-dependent by default: the server installs the ASP.NET Core 10 runtime once, and
# its security patches then arrive through the package manager. --self-contained bundles
# the runtime instead (a larger tarball, no runtime on the server), for a host where
# installing one is undesirable; its runtime patches arrive only when you rebuild and
# redeploy.

set -euo pipefail

SELF_CONTAINED=false
case "${1:-}" in
    "") ;;
    --self-contained) SELF_CONTAINED=true ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 1 ;;
esac
KIND=$([[ $SELF_CONTAINED == true ]] && echo self-contained || echo framework-dependent)

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD="$ROOT/build"
rm -rf "$BUILD"
mkdir -p "$BUILD"

echo "==> publish worker (linux-x64, $KIND)"
dotnet publish "$ROOT/Worker.MT-CalSync" -c Release -r linux-x64 --self-contained "$SELF_CONTAINED" -o "$BUILD/worker-publish"

echo "==> publish self-host portal (linux-x64, $KIND)"
dotnet publish "$ROOT/SelfHost.MT-CalSync" -c Release -r linux-x64 --self-contained "$SELF_CONTAINED" -o "$BUILD/selfhost-publish"

echo "==> bundle schema + deploy + scripts"
cp -r "$ROOT/Core.MT-CalSync/Sql" "$BUILD/worker-publish/sql"
cp -r "$ROOT/deploy" "$BUILD/deploy"
cp -r "$ROOT/scripts" "$BUILD/scripts"

# Never ship a real settings.xml (it holds the DB connection + encryption key).
find "$BUILD" -name settings.xml -delete

echo "==> tar"
tar -czf "$BUILD/mtcalsync-engine.tar.gz" -C "$BUILD" worker-publish selfhost-publish deploy scripts
echo "Done: $BUILD/mtcalsync-engine.tar.gz ($KIND)"
