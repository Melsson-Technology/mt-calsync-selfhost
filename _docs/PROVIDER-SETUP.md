# Provider credentials

MT-CalSync talks to Google Calendar and Microsoft Graph. You can connect an account
two ways. Most self-hosters want **delegated OAuth** — it works for individual
accounts and needs no domain administration.

Enter everything below in the portal's **Settings** page (secrets are stored
AES-encrypted and shown only as a masked status).

Replace `calendar.example.com` with your own portal hostname throughout.

---

## Option A — Delegated OAuth (recommended)

The operator connects each account by clicking **Connect** and consenting. You need
one OAuth "app" per provider that your users consent to.

### Google

1. In the [Google Cloud Console](https://console.cloud.google.com/), create (or pick)
   a project and **enable the Google Calendar API**.
2. **APIs & Services → OAuth consent screen**: configure it. While your app is
   unverified, add yourself as a test user, or publish it (production, unverified is
   fine for a small self-host — verification is only needed for many external users).
3. **Credentials → Create credentials → OAuth client ID → Web application**. Add an
   authorized redirect URI:
   ```
   https://calendar.example.com/oauth/google/callback
   ```
4. Copy the **Client ID** and **Client secret** into Settings
   (`GoogleOAuthClientId` / `GoogleOAuthClientSecret`).

### Microsoft

1. In the [Microsoft Entra admin center](https://entra.microsoft.com/) → **App
   registrations → New registration**. Choose the account types you need (single
   tenant for your org, or multitenant for personal/other tenants — the app uses the
   `/organizations` or `/common` authority accordingly).
2. **Redirect URI** (type *Web*):
   ```
   https://calendar.example.com/oauth/microsoft/callback
   ```
3. **API permissions → Microsoft Graph → Delegated**: add `Calendars.ReadWrite`
   (and `Calendars.ReadWrite.Shared` if you sync shared calendars), plus `User.Read`
   and `offline_access`.
4. **Certificates & secrets → New client secret**. Copy the secret **Value** (not the
   Secret ID) immediately.
5. Put the **Application (client) ID** and the secret **Value** into Settings
   (`MsOAuthClientId` / `MsOAuthClientSecret`).

Then, in the portal: **Calendars → Connect → Connect Google / Connect Microsoft**,
consent, and the account appears connected.

---

## Option B — App-only (org administrators, headless)

No per-user consent; the app impersonates mailboxes with credentials you control.
Requires administrative rights in each provider.

### Google (service account + domain-wide delegation)

1. Create a **service account** in the Cloud Console and generate a **JSON key**.
2. Enable the Calendar API on the project.
3. In the **Google Workspace Admin console → Security → API controls → Domain-wide
   delegation**, authorize the service account's client ID for the scope
   `https://www.googleapis.com/auth/calendar`.
4. In Settings, provide the service-account JSON (`GoogleServiceAccountJson`, or a file
   path via `GoogleServiceAccountJsonPath`). The worker impersonates each mailbox you
   pair by its address.

### Microsoft (application permissions)

1. **App registration** (single tenant is typical for app-only).
2. **API permissions → Microsoft Graph → Application**: add `Calendars.ReadWrite`, then
   **Grant admin consent**.
3. **Certificates & secrets → New client secret**; copy the **Value**.
4. In Settings, provide `GraphTenantId`, `GraphClientId`, and the client secret. Note
   that app-only client-credential tokens are **tenant-locked** — the tenant must be
   the one where the mailbox actually lives (a domain verifies in exactly one Entra
   tenant).

---

## Verify

Run **Test credentials** on the dashboard, or:

```bash
mtcs setup-check
```

It reports the database, both providers (with a live calendar read), and SMTP.
