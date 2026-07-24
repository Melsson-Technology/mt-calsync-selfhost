using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.MTCalSync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace SelfHost.MTCalSync.Controllers
{
	// Account connections: start the OAuth consent flow, receive the callback,
	// list connected accounts, disconnect. Callbacks stay [AllowAnonymous] and
	// re-establish the user from the DataProtection-encrypted state blob (which
	// also carries the PKCE verifier, so no server session is needed) — they never
	// rely on the session cookie, so they work whether or not the browser attached
	// it (auth cookie is SameSite=Lax; Program.cs explains why Strict broke the
	// post-consent redirect chain).
	public class ConnectController : Controller
	{
		private const int StateLifetimeMinutes = 15;
		private readonly IDataProtector _stateProtector;

		public ConnectController(IDataProtectionProvider dataProtection)
		{
			_stateProtector = dataProtection.CreateProtector("MTCalSync.OAuthState");
		}

		// ── connected-accounts page ───────────────────────────────────────────
		[HttpGet]
		public IActionResult Index()
		{
			long userId = PortalAuth.UserId(User);
			ViewBag.Accounts = new OAuthAccount().listByUser(userId);
			ViewBag.MsConfigured = !string.IsNullOrWhiteSpace(Settings.MsOAuthClientId);
			ViewBag.GoogleConfigured = !string.IsNullOrWhiteSpace(Settings.GoogleOAuthClientId);
			ViewBag.UnverifiedAppNotice = Settings.UnverifiedAppNotice;
			return View();
		}

		// Live calendar list for one connected account — the picker's data source
		// and a handy "is this connection actually working?" probe.
		[HttpGet]
		public async Task<IActionResult> Calendars(long accountId)
		{
			long userId = PortalAuth.UserId(User);
			var account = new OAuthAccount().getById(accountId);
			if (account.oauthAccountID == 0 || account.userID != userId) return NotFound();

			ViewBag.Account = account;
			try
			{
				var probe = new ProviderConnection
				{
					provider = account.provider,
					principalEmail = account.principalEmail,
					calendarId = "primary",
					authKind = AuthKinds.DelegatedOauth,
					oauthAccountID = account.oauthAccountID
				};
				ViewBag.Calendars = await ProviderFactory.Create(probe).ListCalendarsAsync();
			}
			catch (NeedsReauthException)
			{
				ViewBag.Error = "This account needs to be reconnected before its calendars can be listed.";
				ViewBag.Calendars = new List<RemoteCalendar>();
			}
			catch (Exception ex)
			{
				Common.writeToLog("ERROR Connect.Calendars:", ex);
				ViewBag.Error = "Couldn't list calendars right now — try again shortly.";
				ViewBag.Calendars = new List<RemoteCalendar>();
			}
			return View();
		}

		// ── start consent ─────────────────────────────────────────────────────
		[HttpGet]
		public IActionResult Start(string provider, string? returnTo = null)
		{
			provider = NormalizeProvider(provider);
			if (provider.Length == 0) return NotFound();

			bool configured = provider == Providers.Google
				? !string.IsNullOrWhiteSpace(Settings.GoogleOAuthClientId)
				: !string.IsNullOrWhiteSpace(Settings.MsOAuthClientId);
			if (!configured)
			{
				TempData["Error"] = "This provider isn't configured yet. (Operator: set the OAuth client id/secret in Settings.)";
				return RedirectToAction("Index");
			}

			string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
			string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
			string state = _stateProtector.Protect(JsonSerializer.Serialize(new StatePayload
			{
				u = PortalAuth.UserId(User),
				c = PortalAuth.CustomerId(User),
				p = provider,
				v = verifier,
				t = DateTime.UtcNow.Ticks,
				// Only carry a same-site local path; anything else is ignored on return.
				r = Url.IsLocalUrl(returnTo) ? returnTo! : string.Empty
			}));

			string url = provider == Providers.Google
				? GoogleOAuthFlow.BuildAuthorizeUrl(state, challenge, RedirectUri(Providers.Google))
				: MsOAuthFlow.BuildAuthorizeUrl(state, challenge, RedirectUri(Providers.M365));
			return Redirect(url);
		}

		// ── callbacks (anonymous — see class comment) ─────────────────────────
		[AllowAnonymous]
		[HttpGet("oauth/google/callback")]
		public async Task<IActionResult> GoogleCallback(string? code, string? state, string? error)
		{
			var payload = ValidateState(state, Providers.Google);
			if (payload == null) return CallbackFailed("The sign-in link expired — please try connecting again.");
			if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
				return CallbackFailed(FriendlyProviderError(error));

			var tokens = await GoogleOAuthFlow.ExchangeCodeAsync(code, payload.v, RedirectUri(Providers.Google));
			if (!tokens.Ok) return CallbackFailed("Google sign-in failed — please try again.");
			if (string.IsNullOrEmpty(tokens.RefreshToken))
				return CallbackFailed("Google didn't issue offline access. Remove MT-CalSync from your Google account's third-party access list and connect again.");

			var claims = GoogleOAuthFlow.ParseIdToken(tokens.IdToken);
			if (string.IsNullOrEmpty(claims.Subject)) return CallbackFailed("Google sign-in failed — please try again.");

			return UpsertAccount(payload, Providers.Google, claims.Subject, tenantId: string.Empty,
				email: claims.Email, displayName: claims.Email, GoogleOAuthFlow.Scopes,
				tokens.RefreshToken, tokens.AccessToken, tokens.ExpiresInSeconds);
		}

		[AllowAnonymous]
		[HttpGet("oauth/microsoft/callback")]
		public async Task<IActionResult> MicrosoftCallback(string? code, string? state, string? error, string? error_description)
		{
			var payload = ValidateState(state, Providers.M365);
			if (payload == null) return CallbackFailed("The sign-in link expired — please try connecting again.");
			if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
				return CallbackFailed(FriendlyProviderError(error, error_description));

			var tokens = await MsOAuthFlow.ExchangeCodeAsync(code, payload.v, RedirectUri(Providers.M365));
			if (!tokens.Ok) return CallbackFailed("Microsoft sign-in failed — please try again.");
			if (string.IsNullOrEmpty(tokens.RefreshToken))
				return CallbackFailed("Microsoft didn't issue offline access — please try connecting again.");

			var claims = MsOAuthFlow.ParseIdToken(tokens.IdToken);
			if (string.IsNullOrEmpty(claims.ObjectId)) return CallbackFailed("Microsoft sign-in failed — please try again.");

			return UpsertAccount(payload, Providers.M365, claims.ObjectId, claims.TenantId,
				claims.Email, string.IsNullOrEmpty(claims.Name) ? claims.Email : claims.Name, MsOAuthFlow.Scopes,
				tokens.RefreshToken, tokens.AccessToken, tokens.ExpiresInSeconds);
		}

		// ── disconnect ────────────────────────────────────────────────────────
		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> Disconnect(long accountId)
		{
			long userId = PortalAuth.UserId(User);
			var account = new OAuthAccount().getById(accountId);
			if (account.oauthAccountID == 0 || account.userID != userId) return NotFound();

			// Connections still referenced by pairs block the disconnect — removing
			// the pairs first is what cleans their mirrored events up properly.
			var connections = new ProviderConnection().listByOAuthAccount(accountId);
			var pc = new ProviderConnection();
			if (connections.Any(c => pc.countReferencingPairs(c.connectionID) > 0))
			{
				TempData["Error"] = "This account still has sync pairs. Remove those pairs first, then disconnect.";
				return RedirectToAction("Index");
			}

			if (account.provider == Providers.Google)
				await GoogleOAuthFlow.RevokeAsync(account.decryptRefreshToken());
			// Microsoft has no per-app revoke endpoint: deleting our stored tokens ends
			// access (the surviving access token dies within the hour). Users can also
			// revoke via myapps.microsoft.com — noted in the privacy policy.

			foreach (var conn in connections) pc.delete(conn.connectionID);
			account.delete(accountId);
			Common.writeToLog($"Disconnected oauth_account {accountId} ({account.provider} {account.principalEmail}) for user {userId}");
			TempData["Info"] = $"{(account.provider == Providers.Google ? "Google" : "Microsoft")} account disconnected and its stored tokens deleted.";
			return RedirectToAction("Index");
		}

		// ── helpers ───────────────────────────────────────────────────────────
		private sealed class StatePayload
		{
			public long u { get; set; }      // userID
			public long c { get; set; }      // customerID
			public string p { get; set; } = string.Empty;   // provider
			public string v { get; set; } = string.Empty;   // PKCE code verifier
			public long t { get; set; }      // issued at (ticks UTC)
			public string r { get; set; } = string.Empty;   // returnTo (local path; carried so the
															 // onboarding guide gets the user back)
		}

		private StatePayload? ValidateState(string? state, string expectedProvider)
		{
			if (string.IsNullOrEmpty(state)) return null;
			try
			{
				var payload = JsonSerializer.Deserialize<StatePayload>(_stateProtector.Unprotect(state));
				if (payload == null || payload.u <= 0 || payload.p != expectedProvider) return null;
				if (new DateTime(payload.t, DateTimeKind.Utc) < DateTime.UtcNow.AddMinutes(-StateLifetimeMinutes)) return null;
				return payload;
			}
			catch { return null; }   // tampered/expired protector payload
		}

		private IActionResult UpsertAccount(StatePayload payload, string provider, string providerAccountId,
			string tenantId, string email, string displayName, string scopes,
			string refreshToken, string accessToken, int expiresInSeconds)
		{
			var existing = new OAuthAccount().getByProviderAccount(provider, providerAccountId);
			if (existing.oauthAccountID > 0 && existing.userID != payload.u)
				return CallbackFailed("That account is already connected to a different MT-CalSync login.");

			var account = existing.oauthAccountID > 0 ? existing : new OAuthAccount();
			account.userID = payload.u;
			account.customerID = payload.c;
			account.provider = provider;
			account.providerAccountId = providerAccountId;
			account.tenantId = tenantId;
			account.principalEmail = email;
			account.displayName = displayName;
			account.scopesGranted = scopes;
			account.refreshTokenEnc = Encryption.Encrypt(refreshToken);
			account.accessTokenEnc = string.IsNullOrEmpty(accessToken) ? string.Empty : Encryption.Encrypt(accessToken);
			account.accessTokenExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresInSeconds));
			account.status = OAuthAccount.StatusConnected;
			account.lastRefreshAt = DateTime.UtcNow;
			account.save();

			Common.writeToLog($"OAuth account {(existing.oauthAccountID > 0 ? "reconnected" : "connected")}: {provider} {email} (user {payload.u})");
			TempData["Info"] = $"{(provider == Providers.Google ? "Google" : "Microsoft")} account {email} connected.";
			// Return to where the flow started (the onboarding guide passes /Dashboard);
			// re-validate local-only since the value round-tripped through the client.
			if (!string.IsNullOrEmpty(payload.r) && Url.IsLocalUrl(payload.r))
				return LocalRedirect(payload.r);
			return RedirectToAction("Index");
		}

		private IActionResult CallbackFailed(string message)
		{
			TempData["Error"] = message;
			return RedirectToAction("Index");
		}

		private static string FriendlyProviderError(string? error, string? description = null)
		{
			if (error == "access_denied") return "You cancelled the connection — nothing was changed.";
			// Tenant-admin consent blocks (Entra) deserve a specific explanation.
			if ((description ?? string.Empty).Contains("AADSTS65001") || (description ?? string.Empty).Contains("AADSTS650052") ||
				(description ?? string.Empty).Contains("AADSTS90094"))
				return "Your organization requires admin approval for new apps, and this app hasn't been approved yet. Ask your Microsoft 365 admin, or connect a personal/work account from a tenant that allows user consent.";
			return "The provider reported an error — please try connecting again.";
		}

		private string RedirectUri(string provider)
		{
			string baseUrl = string.IsNullOrWhiteSpace(Settings.PublicBaseUrl)
				? $"{Request.Scheme}://{Request.Host}"
				: Settings.PublicBaseUrl;
			return $"{baseUrl}/oauth/{(provider == Providers.Google ? "google" : "microsoft")}/callback";
		}

		private static string NormalizeProvider(string? provider) => (provider ?? string.Empty).ToLowerInvariant() switch
		{
			"google" => Providers.Google,
			"microsoft" or "m365" or "ms" => Providers.M365,
			_ => string.Empty
		};

		private static string Base64Url(byte[] bytes) =>
			Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
	}
}
