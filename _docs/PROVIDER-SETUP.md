# Provider credentials

MT-CalSync talks to Google Calendar and Microsoft Graph. You can connect an account
two ways. Most self-hosters want **delegated OAuth** — it works for individual
accounts (Gmail, Google Workspace, and Microsoft 365 work or school accounts) and needs
no domain administration.

Enter everything below in the portal's **Settings** page (secrets are stored
AES-encrypted and never shown in full).

Replace `calendar.example.com` with your own portal hostname throughout.

---

## Option A — Delegated OAuth (recommended)

The operator connects each account by clicking **Connect** and consenting. You need
one OAuth "app" per provider that your users consent to.

### Google

1. In the [Google Cloud Console](https://console.cloud.google.com/), create (or pick)
   a project and **enable the Google Calendar API**.
2. **APIs & Services → OAuth consent screen**: configure it and **publish it** (status
   "In production"). Unverified is fine for a small self-host, since verification only
   matters once many outside users sign in. Don't leave it in "Testing": Google expires a
   Testing app's refresh tokens after 7 days, so every account would need reconnecting
   each week. The app asks for `openid`, your email address,
   `https://www.googleapis.com/auth/calendar.events` and
   `https://www.googleapis.com/auth/calendar.calendarlist.readonly`.
3. **Credentials → Create credentials → OAuth client ID → Web application**. Add an
   authorized redirect URI:
   ```
   https://calendar.example.com/oauth/google/callback
   ```
4. Copy the **Client ID** and **Client secret** into Settings
   (`GoogleOAuthClientId` / `GoogleOAuthClientSecret`).

### Microsoft

1. In the [Microsoft Entra admin center](https://entra.microsoft.com/) → **App
   registrations → New registration**. For supported account types choose **Accounts in
   any organizational directory** (multitenant), even if only your own organization will
   use it. The app signs in through Microsoft's `/organizations` endpoint, which a
   single-tenant registration doesn't accept. Personal Microsoft accounts (outlook.com,
   hotmail.com) can't connect.
2. **Redirect URI** (type *Web*):
   ```
   https://calendar.example.com/oauth/microsoft/callback
   ```
3. **API permissions → Microsoft Graph → Delegated**: add `Calendars.ReadWrite` and
   `Calendars.ReadWrite.Shared` (the app always asks for both), plus `User.Read` and
   `offline_access`.
4. **Certificates & secrets → New client secret**. Copy the secret **Value** (not the
   Secret ID) immediately.
5. Put the **Application (client) ID** and the secret **Value** into Settings
   (`MsOAuthClientId` / `MsOAuthClientSecret`).

Then, in the portal: **Calendars → Connect → Connect Google / Connect Microsoft**,
consent, and the account appears connected.

### Redirect URIs and Public base URL

The portal builds each redirect URI from the address the browser used, as passed on by
your proxy. The sample nginx config passes both the host name and the https scheme, so
the URIs above come out right. If your proxy doesn't, or the portal is reached on a
non-standard port, set **Public base URL** in Settings (for example
`https://calendar.example.com`) so the redirect URIs match the ones you registered.

---

## Option B — App-only (org administrators, headless)

No per-user consent; the app impersonates mailboxes with credentials you control.
Requires administrative rights in each provider. The portal's **Shared calendars** page
works through these credentials too, so it needs Option B even if you connect your own
calendars with Option A.

### Google (service account + domain-wide delegation)

1. Create a **service account** in the Cloud Console and generate a **JSON key**.
2. Enable the Calendar API on the project.
3. In the **Google Workspace Admin console → Security → API controls → Domain-wide
   delegation**, authorize the service account's client ID for the scope
   `https://www.googleapis.com/auth/calendar`.
4. In Settings, provide the service-account JSON (`GoogleServiceAccountJson`, or a file
   path via `GoogleServiceAccountJsonPath`). A key file has to be readable by the
   `mtcalsync` service account:
   `sudo chown mtcalsync:mtcalsync /etc/mtcalsync/google-sa.json && sudo chmod 0600 /etc/mtcalsync/google-sa.json`.
   The worker impersonates each mailbox you pair by its address.

### Microsoft (application permissions)

1. **App registration** (single tenant is typical for app-only).
2. **API permissions → Microsoft Graph → Application**: add `Calendars.ReadWrite`, then
   **Grant admin consent**.
3. **Certificates & secrets → New client secret**; copy the **Value**.
4. In Settings, provide `GraphTenantId`, `GraphClientId`, and the client secret. Note
   that app-only client-credential tokens are **tenant-locked** — the tenant must be
   the one where the mailbox actually lives (a domain verifies in exactly one Entra
   tenant).

### App-credential pairs

Create them with the operator form at the bottom of the **Pairs** page, or with
`mtcs add-pair --m365-email you@yourco.com --google-email you@yourco.com`. New ones start
paused: preview with `mtcs sync --pair N --dry-run`, then resume the pair. Series-mode
recurrence (`--recurrence series`) is available here.

---

## Verify

Once a sync pair exists, run **Test credentials** on the portal's **System** page, or:

```bash
mtcs setup-check
```

It reports the database and your settings, reads a few days of events from each calendar in
a pair, and checks that SMTP is configured. Before any pair exists no calendar can be read, so
it checks the database and settings only and says so. `mtcs test-email` sends a real message.
