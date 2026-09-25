# Installing MT-CalSync (self-host)

MT-CalSync runs as a systemd worker (the sync tick) plus a small Kestrel admin
portal, backed by MySQL 8. It's a framework-dependent .NET 8 app.

## 1. Server prerequisites

You need the .NET 8 ASP.NET runtime, MySQL 8.0 or later (MariaDB is not supported), and
optionally nginx and certbot for TLS. The steps differ by distribution.

### Ubuntu 24.04 or 22.04

```bash
sudo apt-get update
sudo apt-get install -y aspnetcore-runtime-8.0
sudo apt-get install -y mysql-server
# Optional: TLS-terminating reverse proxy
sudo apt-get install -y nginx certbot python3-certbot-nginx
```

### Debian 12

Debian packages neither .NET nor MySQL (its `default-mysql-server` is MariaDB), so both
come from their vendors' own repositories:

```bash
sudo apt-get update
sudo apt-get install -y gnupg curl

# .NET 8, from Microsoft's package feed
curl -fsSL -o /tmp/packages-microsoft-prod.deb https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb
sudo dpkg -i /tmp/packages-microsoft-prod.deb

# MySQL 8.0, from Oracle's APT repository. The signing key comes from a keyserver:
# the copy published at repo.mysql.com has been seen expired.
T="$(mktemp -d)"
gpg --homedir "$T" --keyserver hkps://keyserver.ubuntu.com --recv-keys B7B3B788A8D3785C
gpg --homedir "$T" --export B7B3B788A8D3785C | sudo tee /usr/share/keyrings/mysql.gpg >/dev/null
echo "deb [signed-by=/usr/share/keyrings/mysql.gpg] http://repo.mysql.com/apt/debian bookworm mysql-8.0" \
  | sudo tee /etc/apt/sources.list.d/mysql.list

sudo apt-get update
sudo apt-get install -y aspnetcore-runtime-8.0
# Non-interactive, so the MySQL root password stays blank and root signs in over the
# Unix socket, which provision.sh relies on. A root password set at the prompt instead
# would stop provision.sh from connecting.
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y mysql-server

# Optional: TLS-terminating reverse proxy
sudo apt-get install -y nginx certbot python3-certbot-nginx
```

## 2. Build a release bundle (on your workstation)

Requires the .NET 8 SDK and PowerShell (`pwsh`).

```bash
git clone https://github.com/Melsson-Technology/mt-calsync-selfhost.git mt-calsync && cd mt-calsync
pwsh ./scripts/build-and-package.ps1
# -> build/mtcalsync-engine.tar.gz  (worker + self-host portal + schema + deploy assets)
scp build/mtcalsync-engine.tar.gz user@server:/tmp/
```

On Windows without PowerShell 7, the built-in Windows PowerShell runs the same script:
`powershell -ExecutionPolicy Bypass -File .\scripts\build-and-package.ps1`.

## 3. Provision + deploy (on the server, as root)

```bash
# Unpack the bundle to reach its deploy scripts. They are run with `bash` because a bundle
# built on Windows carries no executable bits.
mkdir -p /tmp/mtcalsync && tar -xzf /tmp/mtcalsync-engine.tar.gz -C /tmp/mtcalsync
sudo bash /tmp/mtcalsync/deploy/provision.sh           # service user, dirs, DB, key, units
sudo bash /tmp/mtcalsync/deploy/deploy-on-server.sh /tmp/mtcalsync-engine.tar.gz
sudo bash /opt/mtcalsync/deploy/load-schema.sh mtcalsync  # schema load; safe to re-run
```

`provision.sh` creates the `mtcalsync` service user, `/opt/mtcalsync`, a local
database, `/etc/mtcalsync/settings.xml` with a freshly generated `DataEncryptionKey`
(this key encrypts stored credentials — back it up; losing it means re-entering every
secret), and installs the systemd units.

`deploy-on-server.sh` sets ownership and permissions itself rather than taking them from
the bundle: the service account owns only the two publish directories, and everything
else under `/opt/mtcalsync` stays root's.

## 4. Configure

```bash
# A wrapper for the worker CLI, used below and in the README:
alias mtcs='sudo -u mtcalsync dotnet /opt/mtcalsync/worker-publish/Worker.MT-CalSync.dll'

# The portal operator login (single admin; stored as a PBKDF2 hash). It prompts, so the
# password never appears on a command line, where sudo would record it in the system log.
mtcs set-admin-password

# Start the portal + the 1-minute sync timer:
sudo systemctl start mtcalsync-selfhost.service mtcalsync-sync.timer
```

To script it, pipe the password in instead: `printf '%s\n' "$PASSWORD" | mtcs set-admin-password`.

The portal listens only on loopback, `127.0.0.1:5091`. For a quick look without a proxy,
use an SSH tunnel: `ssh -L 5091:127.0.0.1:5091 user@server`, then open
<http://localhost:5091>. To serve it properly, point a reverse proxy at it. A sample is in
`deploy/mtcalsync-selfhost.nginx.conf.sample`. Then obtain TLS:

```bash
sudo certbot --nginx -d calendar.example.com
```

## 5. First run

1. Open the portal and sign in with the operator password.
2. Under **Settings**, enter your Google and Microsoft credentials
   (see [PROVIDER-SETUP.md](PROVIDER-SETUP.md)).
3. Go to **Calendars → Connect**, connect a Google and a Microsoft account.
4. **Pairs → New sync pair**: pick a source calendar, a destination calendar, and the
   direction/fidelity. The first sync runs within ~5 minutes.
5. On the **System** page, run **Test credentials** (or `mtcs setup-check`). It checks the
   database and your settings and reads a few days of events from each calendar in a pair.
   Run before any pair exists, it can check only the database and settings, and says so.
   SMTP is checked for settings only; `mtcs test-email` sends a real message.

## Local development

```bash
dotnet build MT-CalSync.Engine.sln
dotnet run --project SelfHost.MT-CalSync -- --urls http://localhost:5091
```

Copy `Worker.MT-CalSync/settings.xml.example` to `SelfHost.MT-CalSync/settings.xml` (and to
`Worker.MT-CalSync/settings.xml` to run the CLI) and fill in `MySqlDatabaseConnection` and a
base64 32-byte `DataEncryptionKey` (`openssl rand -base64 32`). The build copies it next to
the binaries. Apply `Core.MT-CalSync/Sql/*.sql` to a throwaway database.

## Notes

- **Backups:** back up the MySQL database and `/etc/mtcalsync/settings.xml`
  (specifically `DataEncryptionKey`). The DataProtection key ring under
  `/etc/mtcalsync/dpkeys` keeps sessions/OAuth-state valid across deploys.
- **Schema:** `load-schema.sh` creates any table that doesn't exist yet and changes nothing
  else, so re-running it is harmless. There is no migration tracking: when a release changes
  an existing table, its CHANGELOG entry says what to apply by hand.
- **Alerts:** configure SMTP in Settings to receive operator emails when a pair fails
  persistently or an OAuth grant needs reconnecting.
