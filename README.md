# MT-CalSync

Two-way calendar sync between **Google Calendar** and **Microsoft 365 / Outlook**,
self-hostable on your own hardware. Link a Google calendar and a Microsoft calendar
and MT-CalSync keeps them mirrored — create/update/delete, recurring series, and
busy-only or full-detail fidelity — with a poll-based worker that runs headless.

This repository is the **open-source engine**: the sync core, a command-line worker,
and a single-operator admin portal. Everything you need to run your own calendar sync.

## Features

- **Bidirectional or one-way** mirroring between one Google and one Microsoft calendar.
- **Recurring events** — instance mode (the robust default) or series mode (RFC 5545
  RRULE). The pair wizard creates instance-mode pairs; series mode is chosen for an
  app-credential pair, in the portal's operator form or with `add-pair --recurrence series`.
- **Full-detail or busy-block** fidelity per pair.
- **Provenance-stamped mirrors** so a sync never echoes its own writes or duplicates
  externally-organized invites; a per-pair circuit breaker and dead-letter queue keep
  one bad event from stalling a pair.
- **Two ways to connect an account:**
  - **Delegated OAuth** — click "Connect Google" / "Connect Microsoft" and consent.
    Works for Gmail and Google Workspace accounts, and for Microsoft 365 work or school
    accounts, including a one-person business tenant. Personal Microsoft accounts
    (outlook.com, hotmail.com) can't connect. Easiest path.
  - **App-only** — a Google service account with domain-wide delegation + a Microsoft
    app registration. For administrators syncing mailboxes across an org, headless. The
    portal's Shared calendars page uses these credentials too.
- **Secrets encrypted at rest** (AES-256-GCM) with a per-deployment key; no third-party
  services required.

## How it works

```
provider ──delta──▶  SyncEngine  ──stamped mirror──▶ provider
                     (Core)
   Worker.MT-CalSync  — CLI + the systemd-timer sync tick
   SelfHost.MT-CalSync — single-operator admin portal (Settings, Connect, Pairs, Dashboard)
   MySQL 8             — pairs, mappings, tokens, run history
```

The worker wakes on a timer, pulls changes from each side, classifies them
(native change vs. our own echo vs. a delete), plans the minimal set of writes, and
applies them to the other calendar. The origin side always wins; a mirror is a
read-only reflection of exactly one origin event.

## Quick start (self-host)

Prerequisites on the server: **.NET 8 ASP.NET runtime**, **MySQL 8.0 or later** (not
MariaDB), and optionally **nginx** for TLS. Full steps are in [_docs/INSTALL.md](_docs/INSTALL.md).

```bash
# 1. On your workstation: clone, build a release bundle, and copy it to the server
git clone https://github.com/Melsson-Technology/mt-calsync-selfhost.git
cd mt-calsync-selfhost
pwsh ./scripts/build-and-package.ps1          # -> build/mtcalsync-engine.tar.gz
scp build/mtcalsync-engine.tar.gz user@server:/tmp/

# 2. On the server: unpack the deploy scripts, then provision, deploy, load the schema
mkdir -p /tmp/mtcalsync && tar -xzf /tmp/mtcalsync-engine.tar.gz -C /tmp/mtcalsync
sudo bash /tmp/mtcalsync/deploy/provision.sh
sudo bash /tmp/mtcalsync/deploy/deploy-on-server.sh /tmp/mtcalsync-engine.tar.gz
sudo bash /opt/mtcalsync/deploy/load-schema.sh mtcalsync

# 3. Set the portal operator password (it prompts), then start the services
alias mtcs='sudo -u mtcalsync dotnet /opt/mtcalsync/worker-publish/Worker.MT-CalSync.dll'
mtcs set-admin-password
sudo systemctl start mtcalsync-selfhost.service mtcalsync-sync.timer
```

The portal listens only on `127.0.0.1:5091`. Put the nginx sample in front of it for
TLS ([_docs/INSTALL.md](_docs/INSTALL.md), step 4), or reach it through an SSH tunnel:
run `ssh -L 5091:127.0.0.1:5091 user@server` and open <http://localhost:5091>.

Then sign in, enter your Google/Microsoft credentials under **Settings**
(see [_docs/PROVIDER-SETUP.md](_docs/PROVIDER-SETUP.md)), connect your calendars, and
create a sync pair. The first sync runs within about five minutes.

`mtcs`, defined in step 3, is a convenience wrapper for the worker CLI:

```bash
alias mtcs='sudo -u mtcalsync dotnet /opt/mtcalsync/worker-publish/Worker.MT-CalSync.dll'
mtcs setup-check          # DB, settings, and a live read of each calendar in a pair
mtcs list-pairs           # the pairs and their calendars
mtcs status               # per-pair health: last success, failures, dead letters
mtcs sync --pair 1 --dry-run
```

## Building & developing

```bash
dotnet build MT-CalSync.Engine.sln           # engine solution
dotnet run --project SelfHost.MT-CalSync -- --urls http://localhost:5091   # portal
```

## Issues, security, and changes

- **Bugs, questions, feature requests** — [open an issue](https://github.com/Melsson-Technology/mt-calsync-selfhost/issues).
  They are very welcome and need no paperwork. See [CONTRIBUTING.md](CONTRIBUTING.md) for
  how the repository is laid out and why pull requests are not open yet.
- **Security issues** — please do not open a public issue. Email
  security@melssontechnology.com; [SECURITY.md](SECURITY.md) sets out the threat model and
  what to expect.
- **What changed between releases** — [CHANGELOG.md](CHANGELOG.md). This repository is
  published as a release log rather than a commit history, so the changelog is the record.

## License

AGPL-3.0. See [LICENSE](LICENSE). If you run a modified version as a network service,
the AGPL requires you to offer your changes' source to its users.
