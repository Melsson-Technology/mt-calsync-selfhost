#!/usr/bin/env bash
#
# provision.sh — one-time self-host setup for MT-CalSync on a Debian/Ubuntu box.
# Creates the service user + dirs, a local MySQL database and its user, the encryption key,
# and installs the systemd units. Idempotent: re-running it repairs rather than breaks.
# Run as root:
#
#   sudo ./provision.sh
#
# Prerequisites (install yourself): the .NET 8 ASP.NET runtime, MySQL 8.0 or later, and
# nginx (optional). See _docs/INSTALL.md, which covers Ubuntu and Debian.
#
# Environment overrides: MTCALSYNC_DB (database name, default mtcalsync), MTCALSYNC_DB_USER
# (default mtcalsync), MTCALSYNC_DB_PASSWORD (rotates the database password; otherwise the
# one already in settings.xml is kept, or a new one is generated on first run).

set -euo pipefail

APP_USER="mtcalsync"
APP_HOME="/opt/mtcalsync"
CONF_DIR="/etc/mtcalsync"
SETTINGS="$CONF_DIR/settings.xml"
DB_NAME="${MTCALSYNC_DB:-mtcalsync}"
DB_USER="${MTCALSYNC_DB_USER:-$APP_USER}"

echo "==> service user + directories"
id -u "$APP_USER" >/dev/null 2>&1 || useradd --system --home "$APP_HOME" --shell /usr/sbin/nologin "$APP_USER"
mkdir -p "$APP_HOME" "$CONF_DIR" "$CONF_DIR/dpkeys"
# /opt/mtcalsync is root's. deploy-on-server.sh gives the service account its two publish
# dirs and nothing else: root runs the scripts kept here, so the account the portal runs
# as must not be able to rename or edit them. An install made by an older version, which
# handed the whole tree over, is put right here and by the next deploy.
chown root:root "$APP_HOME"
chmod 0755 "$APP_HOME"
for d in deploy scripts; do
    if [[ -d "$APP_HOME/$d" ]]; then chown -R root:root "$APP_HOME/$d"; fi
done
chown -R "$APP_USER:$APP_USER" "$CONF_DIR/dpkeys"
chmod 0700 "$CONF_DIR/dpkeys"

echo "==> database '$DB_NAME' and its user '$DB_USER'"

# MySQL, not MariaDB. The app reaches the database through Oracle's MySql.Data driver and is
# built and run against MySQL 8. MariaDB gets part of the way and then fails somewhere far
# less obvious than here, so refuse it up front.
server_version="$(mysql -N -B -e 'SELECT VERSION();')"
case "$server_version" in
    *MariaDB*)
        echo "ERROR: this database server is MariaDB ($server_version)." >&2
        echo "       MT-CalSync needs MySQL 8.0 or later. On Ubuntu: sudo apt-get install -y mysql-server" >&2
        echo "       Debian ships only MariaDB; _docs/INSTALL.md shows how to install MySQL from Oracle's repository." >&2
        exit 1 ;;
esac
if [[ "${server_version%%.*}" -lt 8 ]]; then
    echo "ERROR: MySQL $server_version is too old. MT-CalSync needs MySQL 8.0 or later." >&2
    exit 1
fi

# Password authentication over loopback TCP, with TLS required: the arrangement the hosted
# service runs on, through the same driver.
#
# This script used to create the user IDENTIFIED WITH auth_socket and write a connection
# string with no password. That could never connect. MySql.Data opens a TCP connection for
# Server=localhost, and socket authentication only works over the Unix socket file. The
# CREATE USER error was also swallowed by `|| true`, so provisioning reported success and the
# app failed at its first query.
#
# The password is hex, so it needs no escaping in SQL, in XML, or in a connection string,
# where a ';' would silently cut it short. A password already in settings.xml is reused, so
# re-running this never breaks a working install; MTCALSYNC_DB_PASSWORD rotates it.
existing_conn=""
existing_pass=""
if [[ -f "$SETTINGS" ]]; then
    # First match only, taken in bash rather than with `| head -1`: under pipefail a head that
    # exits early can hand sed a SIGPIPE and fail the whole assignment.
    existing_conn="$(sed -n 's#.*<MySqlDatabaseConnection>\(.*\)</MySqlDatabaseConnection>.*#\1#p' "$SETTINGS")"
    existing_conn="${existing_conn%%$'\n'*}"
    IFS=';' read -ra parts <<<"$existing_conn"
    for part in "${parts[@]}"; do
        k="${part%%=*}"; k="${k//[[:space:]]/}"
        case "${k,,}" in pwd|password) existing_pass="${part#*=}" ;; esac
    done
fi
DB_PASS="${MTCALSYNC_DB_PASSWORD:-${existing_pass:-$(openssl rand -hex 24)}}"
if [[ ! "$DB_PASS" =~ ^[A-Za-z0-9._~-]+$ ]]; then
    echo "ERROR: the database password may use only letters, digits and . _ ~ -" >&2
    echo "       (it is written into SQL, XML and a connection string without escaping)." >&2
    exit 1
fi

# The plugin is named rather than left to the server's default, so an account created by
# the old auth_socket version of this script is converted, not merely re-passworded. Both
# hosts are granted because a TCP connection to 127.0.0.1 can match either.
mysql <<SQL
CREATE DATABASE IF NOT EXISTS \`$DB_NAME\` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER IF NOT EXISTS '$DB_USER'@'localhost' IDENTIFIED WITH caching_sha2_password BY '$DB_PASS';
CREATE USER IF NOT EXISTS '$DB_USER'@'127.0.0.1' IDENTIFIED WITH caching_sha2_password BY '$DB_PASS';
ALTER USER '$DB_USER'@'localhost' IDENTIFIED WITH caching_sha2_password BY '$DB_PASS';
ALTER USER '$DB_USER'@'127.0.0.1' IDENTIFIED WITH caching_sha2_password BY '$DB_PASS';
GRANT ALL PRIVILEGES ON \`$DB_NAME\`.* TO '$DB_USER'@'localhost';
GRANT ALL PRIVILEGES ON \`$DB_NAME\`.* TO '$DB_USER'@'127.0.0.1';
SQL

# Prove it the way the app will connect, not the way root does. A password in a file rather
# than on the command line, so it never appears in `ps`.
cnf="$(mktemp)"; chmod 0600 "$cnf"
printf '[client]\nuser=%s\npassword=%s\nhost=127.0.0.1\nport=3306\nprotocol=TCP\nssl-mode=REQUIRED\n' \
    "$DB_USER" "$DB_PASS" > "$cnf"
if mysql --defaults-extra-file="$cnf" -N -B -e 'SELECT 1;' "$DB_NAME" >/dev/null 2>&1; then
    rm -f "$cnf"
    echo "    verified: '$DB_USER' reaches '$DB_NAME' over TCP with TLS, as the app will"
else
    err="$(mysql --defaults-extra-file="$cnf" -N -B -e 'SELECT 1;' "$DB_NAME" 2>&1 || true)"
    rm -f "$cnf"
    echo "ERROR: '$DB_USER' cannot connect to '$DB_NAME' over TCP with TLS: $err" >&2
    exit 1
fi

CONN="Server=127.0.0.1;Port=3306;Database=$DB_NAME;Uid=$DB_USER;Pwd=$DB_PASS;SslMode=Required;"

echo "==> settings.xml (+ DataEncryptionKey)"
if [[ ! -f "$SETTINGS" ]]; then
    KEY="$(openssl rand -base64 32)"
    ( umask 0137
      cat > "$SETTINGS" <<XML
<settings>
  <MySqlDatabaseConnection>$CONN</MySqlDatabaseConnection>
  <DataEncryptionKey>$KEY</DataEncryptionKey>
</settings>
XML
    )
    chown "$APP_USER:$APP_USER" "$SETTINGS"; chmod 0640 "$SETTINGS"
    echo "    wrote $SETTINGS (fresh DataEncryptionKey generated)"
else
    grep -q "<DataEncryptionKey>" "$SETTINGS" || {
        KEY="$(openssl rand -base64 32)"
        sed -i "s#</settings>#  <DataEncryptionKey>$KEY</DataEncryptionKey>\n</settings>#" "$SETTINGS"
        echo "    added a DataEncryptionKey to existing $SETTINGS"
    }

    # Rewrite the connection string only when it has no password (the old auth_socket
    # layout) or the password is being rotated. Anything else is left exactly as it is.
    if [[ -z "$existing_pass" || -n "${MTCALSYNC_DB_PASSWORD:-}" ]] && [[ "$existing_conn" != "$CONN" ]]; then
        backup="$SETTINGS.pre-provision-$(date -u +%Y%m%d-%H%M%S)"
        cp -a "$SETTINGS" "$backup"
        key_before="$(grep '<DataEncryptionKey>' "$SETTINGS")"
        case "$(grep -c '<MySqlDatabaseConnection>' "$SETTINGS" || true)" in
            0) sed -i "s#</settings>#  <MySqlDatabaseConnection>$CONN</MySqlDatabaseConnection>\n</settings>#" "$SETTINGS" ;;
            1) sed -i "s#<MySqlDatabaseConnection>.*</MySqlDatabaseConnection>#<MySqlDatabaseConnection>$CONN</MySqlDatabaseConnection>#" "$SETTINGS" ;;
            *) echo "ERROR: $SETTINGS has more than one MySqlDatabaseConnection; fix it by hand." >&2; exit 1 ;;
        esac
        # The DataEncryptionKey decrypts every stored credential, and losing it is permanent,
        # so it is compared, not trusted, after the edit.
        if [[ "$(grep '<DataEncryptionKey>' "$SETTINGS")" != "$key_before" ]] \
           || [[ "$(grep -F -c "<MySqlDatabaseConnection>$CONN</MySqlDatabaseConnection>" "$SETTINGS" || true)" -ne 1 ]]; then
            cp -a "$backup" "$SETTINGS"
            echo "ERROR: rewriting the connection string did not go as expected; $SETTINGS restored." >&2
            exit 1
        fi
        echo "    connection string now uses the database password (previous file: $backup)"
    fi
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
echo "  2) sudo bash $APP_HOME/deploy/load-schema.sh $DB_NAME"
echo "  3) Set the portal operator login (it prompts; mtcs is the alias in the README):"
echo "       sudo -u $APP_USER dotnet $APP_HOME/worker-publish/Worker.MT-CalSync.dll set-admin-password"
echo "  4) systemctl start mtcalsync-selfhost.service && systemctl start mtcalsync-sync.timer"
echo "  5) Open the portal, enter your provider credentials in Settings, connect calendars."
