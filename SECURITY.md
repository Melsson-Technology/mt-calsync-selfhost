# Security

## Reporting a vulnerability

Email **security@melssontechnology.com** with a description, the affected version, and
reproduction steps. Please do not open a public issue for anything exploitable.

You should get an acknowledgement within **3 working days** and an assessment within **10**.
If a fix is warranted we will agree a disclosure timeline with you, and credit you unless
you would rather we did not.

---

## Threat model - please read before reporting

MT-CalSync holds long-lived credentials for other people's calendars and writes to them
unattended. Several behaviours below look alarming out of context and are deliberate.
Knowing which is which saves everyone time.

### The operator is fully trusted, and there are no roles

The self-host portal has exactly one identity. `PortalAuth` resolves every request to
customer 1 / user 1, and `IsAdmin()` returns `true` unconditionally - there is no Viewer,
no Editor, and no second account to escalate from. Anyone who can sign in can:

- read and change every setting, including the stored provider credentials
- see every connected calendar and every event the engine has mirrored
- create, pause and tear down pairs, which deletes mirrored events

**Do not give the portal password to anyone you would not give the database to.** Reports
amounting to "an authenticated operator can do operator things" are not vulnerabilities.

This is a genuine difference from the hosted service, which is multi-tenant and enforces
per-customer isolation. The self-host build deliberately collapses that, because a
single-operator install has nothing to isolate from.

### App-only credentials are as powerful as the directory allows

The app-only connection mode exists so an administrator can sync mailboxes headlessly
across an organisation. That power is the point, and it is not something the engine can
constrain:

- A **Google service account with domain-wide delegation** can impersonate any user in the
  Workspace domain, for every scope you granted it. The engine impersonates one mailbox at
  a time, but the credential itself is not limited to the mailboxes you configured.
- A **Microsoft Graph app registration with application permissions** reads and writes
  calendars tenant-wide, not only the ones a pair names.

Grant the narrowest calendar scopes that work, keep the service-account JSON at `0600`,
and prefer delegated OAuth wherever an org-wide credential is not actually needed. "The
service account can reach a mailbox I did not configure a pair for" is how domain-wide
delegation works, not a flaw in this software.

### Tearing down a pair deletes events, on purpose

`remove-pair` deletes the events that pair mirrored, and `sweep-strays` and `purge-mirror`
exist to clean up after interrupted teardowns. These are destructive by design - a pair you
remove should not leave a frozen copy of your calendar behind.

What makes that safe is provenance: every event the engine writes carries a hidden stamp
(Graph `singleValueExtendedProperties`, Google `extendedProperties.private`) plus a row in
`event_mapping`, and only stamped events are eligible for deletion. Native events are
excluded structurally, not heuristically.

**A case where the engine modifies or deletes an event it did not create is a serious
vulnerability - please report it.** That is the single invariant the whole design rests on.

### Mirrors never notify anyone

Mirrored events carry no attendees, and Google writes use `sendUpdates=none`. Syncing your
calendar must never mail the people on your meetings. **If you find a path where a sync
sends a notification or invitation to an attendee, report it** - that is a privacy failure
even though nothing is technically "leaked".

### Edits on a mirror are overwritten

A mirror is a read-only reflection of exactly one origin event. Editing the copy does not
propagate back, and the change is overwritten when the origin next changes. This is
deliberate loop-safety, not data loss - edit at the origin.

### Busy-only fidelity limits what is written, not what is read

Busy-only pairs write opaque blocks that carry no title, location or description. The
engine still reads full event detail from the origin in order to decide what to write, and
that detail passes through the process and its logs at debug verbosity. Busy-only is a
control over what reaches the *far calendar*, not a guarantee about what the engine sees.

### There is no inbound callback surface

The engine polls; it does not subscribe to provider webhooks. There is no public callback
endpoint to secure, no subscription renewal lifecycle, and nothing on the internet needs to
reach the worker at all. The portal binds `http://127.0.0.1:5091` and expects nginx in
front of it for TLS. If you bind it to a public interface, that is your decision to defend.

---

## What we do consider a vulnerability

- Anything reaching the portal **without authentication** that should not.
- Authentication bypass, session fixation, or cookie forgery against the portal.
- Stored secrets recoverable **without** `DataEncryptionKey`.
- The engine writing to, modifying, or deleting an event it did not create (see above).
- Any sync path that notifies or invites an attendee.
- Events from one connection or pair appearing on a calendar belonging to another.
- Cross-site scripting, CSRF on a state-changing endpoint, or SQL injection.
- Access tokens, refresh tokens, or client secrets written to logs in recoverable form.

## Secrets at rest

OAuth refresh tokens, the Graph client secret, the Google service-account JSON, and the
SMTP password are encrypted with **AES-256-GCM** before being written to the database. The
key is `DataEncryptionKey` in `settings.xml` - base64 of 32 random bytes, generated per
deployment with `openssl rand -base64 32`. The file is mode `0640`, owned by the service
account.

Three consequences worth stating plainly:

1. A stolen database **alone** does not yield those secrets.
2. A stolen database **plus `settings.xml`** yields all of them. They are not separate
   trust domains - store and back them up accordingly.
3. **Losing the key is unrecoverable.** Every stored secret must then be re-entered by
   hand, and every connected account reconnected. Back the key up *with* the database, and
   verify the backup before you need it.

## Supported versions

This project is pre-1.0. Security fixes land on `main`; there are no maintained release
branches yet.
