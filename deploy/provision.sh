#!/usr/bin/env bash
#
# provision.sh — one-time self-host setup for MT-CalSync on a Debian/Ubuntu box.
# Creates the service user + dirs, a local MySQL database, the encryption key, and
# installs the systemd units. Idempotent where practical. Run as root:
#
#   sudo ./provision.sh
#
# Prerequisites (install yourself): the .NET 8 ASP.NET runtime + MySQL/MariaDB +
# nginx (optional). See engine/_docs/INSTALL.md.

set -euo pipefail

APP_USER="mtcalsync"
APP_HOME="/opt/mtcalsync"
CONF_DIR="/etc/mtcalsync"
DB_NAME="${MTCALSYNC_DB:-mtcalsync}"

echo "==> service user + directories"
id -u "$APP_USER" >/dev/null 2>&1 || useradd --system --home "$APP_HOME" --shell /usr/sbin/nologin "$APP_USER"
mkdir -p "$APP_HOME" "$CONF_DIR" "$CONF_DIR/dpkeys"
chown -R "$APP_USER:$APP_USER" "$APP_HOME"
chown -R "$APP_USER:$APP_USER" "$CONF_DIR/dpkeys"
chmod 0700 "$CONF_DIR/dpkeys"

echo "==> database '$DB_NAME' (local socket auth)"
mysql -e "CREATE DATABASE IF NOT EXISTS \`$DB_NAME\` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;"
# Grant the service user access over the local socket (adjust to taste; least-privilege
# runtime grants are recommended once you are comfortable).
mysql -e "CREATE USER IF NOT EXISTS '$APP_USER'@'localhost' IDENTIFIED WITH auth_socket;" 2>/dev/null || true
mysql -e "GRANT ALL PRIVILEGES ON \`$DB_NAME\`.* TO '$APP_USER'@'localhost'; FLUSH PRIVILEGES;"

echo "==> settings.xml (+ DataEncryptionKey)"
SETTINGS="$CONF_DIR/settings.xml"
if [[ ! -f "$SETTINGS" ]]; then
    KEY="$(openssl rand -base64 32)"
    cat > "$SETTINGS" <<XML
<settings>
  <MySqlDatabaseConnection>Server=localhost;Database=$DB_NAME;Uid=$APP_USER;</MySqlDatabaseConnection>
  <DataEncryptionKey>$KEY</DataEncryptionKey>
</settings>
XML
    chown "$APP_USER:$APP_USER" "$SETTINGS"; chmod 0640 "$SETTINGS"
    echo "    wrote $SETTINGS (fresh DataEncryptionKey generated)"
else
    grep -q "<DataEncryptionKey>" "$SETTINGS" || {
        KEY="$(openssl rand -base64 32)"
        sed -i "s#</settings>#  <DataEncryptionKey>$KEY</DataEncryptionKey>\n</settings>#" "$SETTINGS"
        echo "    added a DataEncryptionKey to existing $SETTINGS"
    }
fi

echo "==> systemd units"
install -m0644 "$(dirname "$0")/mtcalsync-worker@.service"  /etc/systemd/system/
install -m0644 "$(dirname "$0")/mtcalsync-sync.timer"       /etc/systemd/system/
install -m0644 "$(dirname "$0")/mtcalsync-selfhost.service" /etc/systemd/system/
[[ -f "$CONF_DIR/selfhost.env" ]] || install -m0644 "$(dirname "$0")/selfhost.env.example" "$CONF_DIR/selfhost.env"
systemctl daemon-reload
systemctl enable mtcalsync-sync.timer mtcalsync-selfhost.service

echo
echo "Provisioned. Next:"
echo "  1) Deploy binaries to $APP_HOME (see build-and-package.ps1 + deploy-on-server.sh)."
echo "  2) sudo $APP_HOME/scripts/load-schema.sh $DB_NAME"
echo "  3) mtcs set-admin-password --password <value>   # the portal operator login"
echo "  4) systemctl start mtcalsync-selfhost.service && systemctl start mtcalsync-sync.timer"
echo "  5) Open the portal, enter your provider credentials in Settings, connect calendars."
