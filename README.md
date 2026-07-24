# MT-CalSync

Two-way calendar sync between **Google Calendar** and **Microsoft 365 / Outlook**,
self-hostable on your own hardware. Link a Google calendar and a Microsoft calendar
and MT-CalSync keeps them mirrored — create/update/delete, recurring series, and
busy-only or full-detail fidelity — with a poll-based worker that runs headless.

This repository is the **open-source engine**: the sync core, a command-line worker,
and a single-operator admin portal. Everything you need to run your own calendar sync.

## Features

- **Bidirectional or one-way** mirroring between one Google and one Microsoft calendar.
- **Recurring events** — instance mode (robust) or series mode (RFC 5545 RRULE).
- **Full-detail or busy-block** fidelity per pair.
- **Provenance-stamped mirrors** so a sync never echoes its own writes or duplicates
  externally-organized invites; a per-pair circuit breaker and dead-letter queue keep
  one bad event from stalling a pair.
- **Two ways to connect an account:**
  - **Delegated OAuth** — click "Connect Google" / "Connect Microsoft" and consent.
    Works for individual Gmail / personal-tenant accounts. Easiest path.
  - **App-only** — a Google service account with domain-wide delegation + a Microsoft
    app registration. For administrators syncing mailboxes across an org, headless.
- **Secrets encrypted at rest** (AES-256-GCM) with a per-deployment key; no third-party
  services required.

## How it works

```
provider ──delta──▶  SyncEngine  ──stamped mirror──▶ provider
                     (Core)
   Worker.MT-CalSync  — CLI + the systemd-timer sync tick
   SelfHost.MT-CalSync — single-operator admin portal (Settings, Connect, Pairs, Dashboard)
   MySQL / MariaDB     — pairs, mappings, tokens, run history
```

The worker wakes on a timer, pulls changes from each side, classifies them
(native change vs. our own echo vs. a delete), plans the minimal set of writes, and
applies them to the other calendar. The origin side always wins; a mirror is a
read-only reflection of exactly one origin event.

## Quick start (self-host)

Prerequisites on the server: **.NET 8 ASP.NET runtime**, **MySQL or MariaDB**, and
optionally **nginx** for TLS. Full steps are in [_docs/INSTALL.md](_docs/INSTALL.md).

```bash
# 1. On your workstation: build a release bundle
pwsh ./scripts/build-and-package.ps1          # -> build/mtcalsync-engine.tar.gz

# 2. On the server (as root): provision, deploy, load the schema
sudo ./deploy/provision.sh
sudo ./deploy/deploy-on-server.sh mtcalsync-engine.tar.gz
sudo /opt/mtcalsync/scripts/load-schema.sh mtcalsync

# 3. Set the portal operator password, then start the services
mtcs set-admin-password --password '<choose-a-strong-password>'
systemctl start mtcalsync-selfhost.service mtcalsync-sync.timer
```

Then open the portal, enter your Google/Microsoft credentials under **Settings**
(see [_docs/PROVIDER-SETUP.md](_docs/PROVIDER-SETUP.md)), connect your calendars, and
create a sync pair. The first sync runs within about five minutes.

`mtcs` is a convenience wrapper for the worker CLI:

```bash
alias mtcs='sudo -u mtcalsync dotnet /opt/mtcalsync/worker-publish/Worker.MT-CalSync.dll'
mtcs setup-check          # validate DB, credentials, live calendar access, SMTP
mtcs list-pairs | status  # inspect
mtcs sync --pair 1 --dry-run
```

## Building & developing

```bash
dotnet build MT-CalSync.Engine.sln           # engine solution
dotnet run --project SelfHost.MT-CalSync     # portal on http://localhost:5091
```

## License

AGPL-3.0. See [LICENSE](LICENSE). If you run a modified version as a network service,
the AGPL requires you to offer your changes' source to its users.
