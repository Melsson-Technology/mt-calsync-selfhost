# Installing MT-CalSync (self-host)

MT-CalSync runs as a systemd worker (the sync tick) plus a small Kestrel admin
portal, backed by MySQL/MariaDB. It's a framework-dependent .NET 8 app.

## 1. Server prerequisites

Debian/Ubuntu example:

```bash
# .NET 8 ASP.NET runtime (see https://learn.microsoft.com/dotnet for your distro)
sudo apt-get install -y aspnetcore-runtime-8.0
# Database
sudo apt-get install -y mariadb-server         # or mysql-server
# Optional: TLS-terminating reverse proxy
sudo apt-get install -y nginx certbot python3-certbot-nginx
```

## 2. Build a release bundle (on your workstation)

Requires the .NET 8 SDK and PowerShell (`pwsh`).

```bash
git clone <this-repo> mt-calsync && cd mt-calsync
pwsh ./scripts/build-and-package.ps1
# -> build/mtcalsync-engine.tar.gz  (worker + self-host portal + schema + deploy assets)
scp build/mtcalsync-engine.tar.gz user@server:/tmp/
```

## 3. Provision + deploy (on the server, as root)

```bash
# Unpack the deploy assets once to run provision from them, or run the copies in the tarball.
sudo ./provision.sh                                   # service user, dirs, DB, key, units
sudo ./deploy-on-server.sh /tmp/mtcalsync-engine.tar.gz
sudo /opt/mtcalsync/scripts/load-schema.sh mtcalsync  # one-time schema load
```

`provision.sh` creates the `mtcalsync` service user, `/opt/mtcalsync`, a local
database, `/etc/mtcalsync/settings.xml` with a freshly generated `DataEncryptionKey`
(this key encrypts stored credentials — back it up; losing it means re-entering every
secret), and installs the systemd units.

## 4. Configure

```bash
# The portal operator login (single admin; stored as a PBKDF2 hash):
mtcs set-admin-password --password '<choose-a-strong-password>'

# Start the portal + the 1-minute sync timer:
sudo systemctl start mtcalsync-selfhost.service mtcalsync-sync.timer
```

Point a reverse proxy at the portal (loopback `127.0.0.1:5091`) — a sample is in
`deploy/mtcalsync-selfhost.nginx.conf.sample`. Then obtain TLS:

```bash
sudo certbot --nginx -d calendar.example.com
```

## 5. First run

1. Open the portal and sign in with the operator password.
2. Under **Settings**, enter your Google and Microsoft credentials
   (see [PROVIDER-SETUP.md](PROVIDER-SETUP.md)).
3. On the dashboard, run **Test credentials** (or `mtcs setup-check`) to confirm live
   access to both providers, the database, and (optionally) SMTP for failure alerts.
4. Go to **Calendars → Connect**, connect a Google and a Microsoft account.
5. **Pairs → New sync pair**: pick a source calendar, a destination calendar, and the
   direction/fidelity. The first sync runs within ~5 minutes.

## Local development

```bash
dotnet build MT-CalSync.Engine.sln
dotnet run --project SelfHost.MT-CalSync      # http://localhost:5091
```

Put a `settings.xml` next to the built assembly (or in the project dir) with at least
`MySqlDatabaseConnection` and a base64 32-byte `DataEncryptionKey`
(`openssl rand -base64 32`). Apply `Core.MT-CalSync/Sql/*.sql` to a throwaway database
whose name contains `dev`/`test`.

## Notes

- **Backups:** back up the MySQL database and `/etc/mtcalsync/settings.xml`
  (specifically `DataEncryptionKey`). The DataProtection key ring under
  `/etc/mtcalsync/dpkeys` keeps sessions/OAuth-state valid across deploys.
- **Schema:** `load-schema.sh` is a one-time operation (plain `CREATE TABLE`, no
  migration tracking). Apply later schema files by hand.
- **Alerts:** configure SMTP in Settings to receive operator emails when a pair fails
  persistently or an OAuth grant needs reconnecting.
