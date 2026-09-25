# Changelog

This repository is published as a release log: each entry here is one published tree, not a
replay of upstream development history. Changes are described for people running the engine,
which is why there is no per-commit log to read.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). This project is
pre-1.0 and does not yet follow semantic versioning - breaking changes will be called out in
their entry.

## [Unreleased]

### Added

- `CONTRIBUTING.md`, `SECURITY.md` and this changelog. Issues and security reports now have a
  documented destination; `SECURITY.md` carries the threat model, including which deliberate
  behaviours are not worth reporting.
- `.gitattributes` normalising line endings, so shell scripts checked out on Windows still
  run on the Linux server.

### Changed

- **`add-pair` and the portal's operator form now create pairs paused.** Both have always said
  to dry-run a new pair first, but the pair was created enabled and the timer ran it within a
  minute. Preview with `sync --pair N --dry-run`, then `resume --pair N` (or Resume in the
  portal). A script that relied on a pair going live when it was created needs that `resume`.
- **A dry run shows its plan even when a live run would trip the circuit breaker.** It used to
  stop at the breaker without showing the plan, and sent an alert email. It now prints the plan,
  with a note that a live run would stop and how to apply it with `--force`. A failed dry run
  prints the reason instead of only logging it, and no dry run sends an alert or a reconnect
  email.
- **A dry run writes nothing.** Classification used to save mapping changes (adoptions,
  re-keys) even in a dry run, the run was recorded in history as the pair's latest "success",
  and a dry-run tick rescheduled every due pair. None of that happens now.
- **`set-admin-password` and `set-secret` prompt for the value.** Passed on the command line,
  as the docs used to show, it ended up in `/var/log/auth.log`: the documented `mtcs` alias runs
  through sudo, which records the whole command line. The prompt doesn't echo, and a script can
  pipe the value in. A value on the command line still works, but don't use it on a shared server.
- **`migrate-secrets` checks rather than converts.** Pre-v2 values can no longer be decrypted, so
  it now confirms that every stored secret (both OAuth client secrets included) is in the current
  format, and names any that has to be re-entered. It exits 1 when it can't read the database,
  which it used to report as "0 failed".

### Fixed

- **A new sync pair could be wired to the wrong connection.** When a pair reused a calendar
  connection that already existed, its id was read from `LAST_INSERT_ID()`, which an upsert
  that updates an existing row doesn't set. The pair got whatever row the database session had
  inserted last, typically the connection created a moment before, so one side of the pair
  pointed at the wrong calendar. The id is now always read back by the connection's unique key.
- **A bundle built on Windows installed world-writable.** Root's `tar` keeps the modes a bundle
  records, and a Windows build records 0666 files and 0777 directories. `deploy-on-server.sh`
  now sets ownership and modes itself: the service account owns only the two publish
  directories, and `/opt/mtcalsync` with its `deploy/` and `scripts/` stays root's, because root
  runs those scripts. `provision.sh` corrects an existing install too.
- Worker and portal logs now survive a deploy. They lived in the swapped-out directory and were
  deleted by the deploy after next.
- `sync --pair N` for a pair that doesn't exist, `pause` or `resume` of one, and a `test-email`
  that wasn't sent now exit 1.
- **"Test credentials" called a settings-only check a pass.** With no sync pair it contacts no
  calendar, yet it showed a green PASS under a button promising a live probe. It now shows
  "Settings only" and says live access is checked once a pair exists, and `setup-check` prints
  the same. Refreshing the result no longer runs the test again. INSTALL.md and
  PROVIDER-SETUP.md now run it after a pair exists, on the System page.
- **Two portal pages were titled "Dashboard".** The operator page is now "System", matching the
  nav. It reports the Google and Microsoft sign-in clients separately from the app-only
  credentials, which it used to label "Microsoft 365" and "Google" and show as incomplete on a
  correctly configured install that uses sign-in.
- **Pairs made with the operator form, `add-pair` or Shared calendars were hidden** from the
  Pairs list and the Dashboard, which kept showing the setup guide over running pairs. Every
  pair is listed now.
- **The Calendars page used hosted-service wording.** It said app verification with Google and
  Microsoft was in progress, which is not true of your own OAuth apps. When a sign-in client is
  missing it now says to add it under Settings, instead of "not available yet".
- **The portal scrolled sideways on phones.** The top bar is about 865px wide on one row. Below
  920px its links now wrap under the brand and sign-out, and wide tables scroll inside their card.
- **Settings showed the last four characters of every secret** while saying secrets were never
  shown back. Short secrets, usually a password someone chose, now show only that they are set.
  Long ones such as API keys keep the four-character hint, and the page says so.
- The failure alert email led with a raw stack trace. It now opens with the pair, the error and
  what happens next, and keeps the trace at the end for a bug report.
- Smaller copy fixes: the pair wizard pointed to an "Accounts page" (the nav says Calendars),
  Shared calendars printed empty names when nothing was set up, and the Remove-pair dialog now
  says only what teardown does.
- The antiforgery cookie is marked Secure over HTTPS, and the portal sends HSTS (30 days) when
  it is served over TLS.
- **Shared calendars needs the app-only credentials, and now says so.** Its mirrors are
  app-credential pairs, so on an install that only uses sign-in they failed on their first sync.
  The page explains what it needs and won't create a pair without it.
- **Install docs that didn't work as written.**
  - INSTALL.md starts with `apt-get update` (without it certbot can't be installed on a fresh
    cloud image) and has a tested Debian 12 recipe. The old "Debian/Ubuntu example" only worked
    on Ubuntu, and provision.sh's MariaDB message named a package Debian doesn't have.
  - The README explains how to reach the portal, which listens only on loopback. Its
    `list-pairs | status` line ran `status` as a shell command, and it claimed support for
    personal Microsoft accounts, which can't connect.
  - PROVIDER-SETUP.md says the Microsoft app must be registered multitenant: sign-in uses the
    `/organizations` endpoint, not "`/organizations` or `/common` accordingly". It also covers
    the scopes requested, the 7-day token expiry of a Google consent screen left in Testing,
    and when to set Public base URL.
  - CONTRIBUTING's `dotnet run` bound :5000 while promising :5091.
  - `settings.xml.example` pointed at files that don't exist, had no `DataEncryptionKey`, and
    carried placeholders that setup-check reported as configured.
  - SECURITY.md now states accurately what an operator can see, which endpoints are anonymous,
    and which secrets are encrypted.
  - The docs said re-running `load-schema.sh` errors (it is a harmless no-op), and the timer's
    comment credited `Persistent=` with catching up missed ticks, which it can't do for this
    kind of timer. `robots.txt` was the hosted site's.

## [2026-09-23] - the self-host install, made to work as written

### Fixed

- **`provision.sh` created a database user the app could never connect as.** The user was
  created `IDENTIFIED WITH auth_socket`, and the connection string it wrote had no password.
  MySql.Data connects over TCP for `Server=localhost`, and socket authentication only works over
  the Unix socket, so the first query failed. The `CREATE USER` error was also suppressed, so
  provisioning reported success. The user now authenticates with a generated password
  (`caching_sha2_password`) over `127.0.0.1` with TLS required. That is the arrangement the
  hosted service runs on. The script proves the connection the way the app will make it before
  it finishes. **Re-running it repairs an install made by the old version:** it converts the
  account and rewrites only the connection string, keeping `DataEncryptionKey` byte-for-byte.
- **`set-admin-password`, `set-secret` and `migrate-secrets` reported success for values never
  saved.** A failed database write was logged and swallowed, and the command still printed
  "Self-host operator password set." and exited 0. On a broken install that meant a password
  that could never sign in, with nothing saying why. They now say what failed and exit 1.
- **MariaDB is refused rather than half-configured.** The engine is built and run against
  MySQL 8 through Oracle's MySql.Data. The docs had offered "MySQL or MariaDB", and INSTALL.md's
  example installed MariaDB. `provision.sh` now stops on MariaDB, or on MySQL older than 8.0,
  with a message saying what to install.
- **The quick start failed at step 2.** The deploy scripts were not executable. `load-schema.sh`
  was run from a directory nothing puts it in. The steps never unpacked the bundle they ran
  scripts from. `mtcs` was used before it was defined, and `dotnet run` was documented on :5091
  while binding :5000. All are fixed, in the README and in INSTALL.md.

## [2026-07-24] - sync correctness

### Fixed

- **Duplicate mirrors after an Exchange id change.** Exchange reissues an event's id on some
  edit and accept operations, so the side-key lookup missed an event already being mirrored -
  and both match-before-create rungs missed it too, because the destination stamp was keyed on
  the old origin id. Editing and reverting an event could triple it. The mapping is now
  recovered by the churn-stable origin `iCalUId` and re-keyed in place.
- **Recurring occurrences arriving as "(no title)".** Graph's `calendarView/delta` expands a
  series into occurrences whose property set is lossy: empty subject, empty `iCalUId`, and
  `isAllDay` dropped to `false`. In instance mode those were mirrored as-is, so a recurring
  all-day event landed on the far side untitled and no longer all-day - and the empty uid also
  defeated the `iCalUID` match rung, duplicating events that already existed natively. Lossy
  occurrences are now re-read in full before they reach the engine; only lossy ones pay the
  extra fetch.

### Added

- `inspect` prints a `delta-read:` line whenever the raw delta value diverges from the
  enriched read, which is how the above was diagnosed.

## [2026-07-24] - initial public release

First public release of the MT-CalSync engine under AGPL-3.0: the sync core, the command-line
worker, and the single-operator self-host portal.

- Two-way or one-way sync between a Google calendar and a Microsoft 365 calendar, configured
  per pair, with busy-only or full-detail fidelity.
- Recurring events in instance mode (rolling-window materialisation) or series mode
  (RFC 5545 RRULE translation, including single-occurrence overrides).
- Provenance-stamped mirrors plus an `event_mapping` table, so the engine recognises its own
  writes structurally - no echo loops, and teardown deletes only what it created.
- Delegated OAuth (authorization code + PKCE) or app-only credentials (Google service account
  with domain-wide delegation, Microsoft Graph application permissions).
- Secrets encrypted at rest with AES-256-GCM under a per-deployment key.
- Per-pair circuit breaker, dead-letter quarantine, and per-account failure isolation.
- systemd units, nginx sample, and a provisioning script for a Linux host with MySQL.
