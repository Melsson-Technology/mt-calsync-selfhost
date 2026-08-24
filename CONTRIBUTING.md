# Contributing to MT-CalSync

## Where this lives

Development happens on Melsson Technology's own Gitea, and
[github.com/Melsson-Technology/mt-calsync-selfhost](https://github.com/Melsson-Technology/mt-calsync-selfhost)
is a mirror of it. GitHub is the right place to reach us - use its issue tracker - but it is
pushed to one way, so anything committed there is overwritten by the next sync.

That is also why a pull request opened on GitHub cannot be merged, however good it is. See
below for the reason it would not be merged anyway right now.

## Code contributions are not open yet

**Issues are very welcome** - bug reports, questions, feature requests, "this documentation
is wrong", "this failed against my tenant". Those are the most useful thing you can send
right now, and they need no paperwork:
<https://github.com/Melsson-Technology/mt-calsync-selfhost/issues>.

**Security issues are the exception - please do not open an issue.** Email
**security@melssontechnology.com** instead; [SECURITY.md](SECURITY.md) explains what to
expect and, just as usefully, which behaviours are deliberate and not worth reporting.

**Pull requests are not being accepted yet**, and one opened here will be closed
automatically with a pointer back to this file - not out of rudeness, but because leaving it
open would imply it is being considered. If it is something you would like to fix yourself,
say so in an issue and we will come back to you when this changes.

The reason is licensing, and it is easier to be straight about it than to leave PRs sitting
unanswered. MT-CalSync is AGPL-3.0, and Melsson Technology also offers a hosted version.
That combination works only while Melsson holds the rights to relicense the codebase, which
means an outside contribution needs a contributor agreement granting those rights **before**
it is merged. There is no way to add one retroactively - it would mean tracking down every
past contributor and getting each to agree, and any who declined, or who could not be
reached, would be permanent.

So rather than merge contributions now and create that problem, contributions are closed
until the agreement exists. It is on the roadmap, not a policy of principle.

None of this restricts what the AGPL already grants you. Fork it, modify it, run your own
version, ship it to others under the same licence - that is what the licence is for, and it
needs no permission from us. The rest of this document is written to make that easier as
much as it is written for future contributors.

## Getting started

You need a **.NET 8 SDK** and a **MySQL or MariaDB** you can throw away.

```bash
git clone https://github.com/Melsson-Technology/mt-calsync-selfhost.git
cd mt-calsync-selfhost
dotnet build MT-CalSync.Engine.sln
```

To run the portal against a scratch database, create it, apply
`Core.MT-CalSync/Sql/001_schema.sql`, then copy `settings.xml.example` to `settings.xml`
beside the binaries and fill in the connection string and a fresh `DataEncryptionKey`
(`openssl rand -base64 32`):

```bash
dotnet run --project SelfHost.MT-CalSync      # http://localhost:5091
```

**On tests:** the engine does not ship a public test project yet. The suite that exercises
encryption round-trips, the scheduler's gating and backoff, and the OAuth token custody path
currently lives in the private repository alongside the hosted service, and splitting the
provider-independent parts out is open work. If you are changing engine logic, say in the
issue what you did to convince yourself it was right - that is the substitute for now, and
we would rather know than not.

## Layout

```
Core.MT-CalSync/       the engine: sync core, providers, auth flows, entities, SQL schema
Worker.MT-CalSync/     CLI + the systemd-timer sync tick
SelfHost.MT-CalSync/   single-operator admin portal (Settings, Connect, Pairs, Dashboard)
deploy/                systemd units, nginx sample, provisioning
scripts/               build/package
_docs/                 install and provider setup guides
```

## How the engine fits together

Worth understanding before changing anything under `Core.MT-CalSync/Sync`:

- **Every mirror is stamped.** A mirrored event carries a hidden provenance stamp (Graph
  `singleValueExtendedProperties`, Google `extendedProperties.private`) *and* a row in
  `event_mapping`. That is how the engine recognises its own writes structurally rather than
  by guessing, and it is what makes teardown safe. Do not add a write path that skips it.
- **The origin always wins.** A mirror is a read-only reflection of exactly one origin
  event. Two-way sync is two one-way syncs over different events, never a merge.
- **Match before create.** Before inserting, the engine walks a ladder - destination stamp,
  then `iCalUID`, then create - so an event that already exists on the far side is adopted
  rather than duplicated. Both rungs have failed in the field (Exchange reissues ids on some
  operations; `calendarView/delta` returns occurrences with an empty uid), and both fixes are
  in `SyncEngine` and `GraphCalendarProvider`. Read those before touching the ladder.
- **Recurring series are materialised, not translated.** Instance mode expands a series into
  concrete occurrences over a rolling window rather than converting recurrence rules between
  providers, which is where calendar syncs classically break. Series mode exists and
  translates RRULEs; it is the sharper edge of the two.
- **Failures are isolated per pair.** A circuit breaker parks a failing pair and a dead-letter
  queue quarantines a single bad event, so one poisonous meeting cannot stall a calendar.

## Things that will get a change sent back

- **A write path that does not stamp its mirror.** See above - this is the invariant the
  design rests on, and an unstamped write is indistinguishable from a user's own event.
- **Mirroring attendees, or any write that can notify.** Google writes must pass
  `sendUpdates=none`; Graph mirrors must carry no attendees. Syncing a calendar must never
  mail the people on your meetings.
- **Back-propagating edits from a mirror to its origin.** That is how sync loops are built.
- **Polling harder when a provider is failing.** Throttling and 5xx responses back off; they
  do not retry faster. A struggling tenant does not need more traffic.
- **Widening app-only scopes for convenience.** Domain-wide delegation is already the most
  dangerous credential in the system.

## Style

Comments should explain **why**, not what. The codebase leans on this, particularly where
something looks odd but is deliberate - the reason the match ladder has three rungs, the
reason lossy delta occurrences get a second fetch, the reason teardown trusts stamps and
nothing else. If you work out something subtle, leave the explanation behind.

## Reporting security issues

Please do not open a public issue. See [SECURITY.md](SECURITY.md).
