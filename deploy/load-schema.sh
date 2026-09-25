#!/usr/bin/env bash
#
# load-schema.sh — apply the MT-CalSync engine schema (engine/Core.MT-CalSync/Sql/NNN_*.sql,
# bundled into worker-publish/sql/ by build-and-package.ps1) in numeric order.
#
# Run on the server as root (local MySQL root socket auth):
#   sudo bash /opt/mtcalsync/deploy/load-schema.sh [db-name] [sql-dir]
#
#   db-name  defaults to "mtcalsync"
#   sql-dir  defaults to "/opt/mtcalsync/worker-publish/sql"
#
# NOTE: every table is CREATE TABLE IF NOT EXISTS, so re-running this is a harmless
# no-op. For the same reason it never changes a table that already exists, and there is
# no migration tracking: apply later schema changes by hand.

set -euo pipefail

DB="${1:-mtcalsync}"
SQL_DIR="${2:-/opt/mtcalsync/worker-publish/sql}"

[[ -d "$SQL_DIR" ]] || { echo "No SQL dir at $SQL_DIR — run a deploy first." >&2; exit 1; }

shopt -s nullglob
mapfile -t files < <(printf '%s\n' "$SQL_DIR"/[0-9]*.sql | sort)
[[ ${#files[@]} -gt 0 ]] || { echo "No NNN_*.sql files in $SQL_DIR." >&2; exit 1; }

for f in "${files[@]}"; do
    echo "==> $(basename "$f")"
    mysql "$DB" < "$f"
done

echo "Schema loaded into '$DB' (${#files[@]} files)."
