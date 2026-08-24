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
