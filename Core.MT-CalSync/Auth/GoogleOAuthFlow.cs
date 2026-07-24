using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace Core.MTCalSync
{
	// Google OAuth web-server flow. The consent/exchange legs are plain HTTP (same
	// shape as the Microsoft flow); at runtime the stored refresh token is wrapped
	// in the SDK's UserCredential, which auto-refreshes access tokens and persists
	// them back through OAuthTokenStore. Google refresh tokens don't rotate — but
	// they DO expire after 7 days while the consent screen is in "Testing", which
	// exercises the needs_reauth path until verification moves us to production.
	public static class GoogleOAuthFlow
	{
		private static readonly HttpClient _http = new();

		// Least-privilege per the product spec: event read/write + calendar list for
		// the picker. openid/email identify the account (`sub` is the stable id).
		public const string Scopes =
			"openid email " +
			"https://www.googleapis.com/auth/calendar.events " +
			"https://www.googleapis.com/auth/calendar.calendarlist.readonly";

		public class TokenResult
		{
			public string AccessToken { get; set; } = string.Empty;
			public string RefreshToken { get; set; } = string.Empty;
			public int ExpiresInSeconds { get; set; }
			public string IdToken { get; set; } = string.Empty;
			public string Error { get; set; } = string.Empty;
			public string ErrorDescription { get; set; } = string.Empty;
			public bool Ok => string.IsNullOrEmpty(Error) && !string.IsNullOrEmpty(AccessToken);
		}

		public static string BuildAuthorizeUrl(string state, string codeChallenge, string redirectUri)
		{
			var q = new Dictionary<string, string>
			{
				{ "client_id", Settings.GoogleOAuthClientId },
				{ "redirect_uri", redirectUri },
				{ "response_type", "code" },
				{ "scope", Scopes },
				// offline + consent guarantees a refresh token on every (re)connect.
				{ "access_type", "offline" },
				{ "prompt", "consent" },
				{ "state", state },
				{ "code_challenge", codeChallenge },
				{ "code_challenge_method", "S256" },
				{ "include_granted_scopes", "true" }
			};
			return "https://accounts.google.com/o/oauth2/v2/auth?" +
				string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
		}

		public static async Task<TokenResult> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri)
		{
			var result = new TokenResult();
			try
			{
				using var content = new FormUrlEncodedContent(new Dictionary<string, string>
				{
					{ "client_id", Settings.GoogleOAuthClientId },
					{ "client_secret", Settings.EffectiveGoogleOAuthClientSecret },
					{ "grant_type", "authorization_code" },
					{ "code", code },
					{ "redirect_uri", redirectUri },
					{ "code_verifier", codeVerifier }
				});
				using var resp = await _http.PostAsync("https://oauth2.googleapis.com/token", content);
				string body = await resp.Content.ReadAsStringAsync();
				using var doc = JsonDocument.Parse(body);
				var root = doc.RootElement;

				if (root.TryGetProperty("error", out var err))
				{
					result.Error = err.GetString() ?? "unknown";
					result.ErrorDescription = root.TryGetProperty("error_description", out var ed) ? (ed.GetString() ?? "") : "";
					Common.writeToLog($"Google token endpoint error ({result.Error}): {result.ErrorDescription}");
					return result;
				}
				result.AccessToken = root.TryGetProperty("access_token", out var at) ? (at.GetString() ?? "") : "";
				result.RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? (rt.GetString() ?? "") : "";
				result.ExpiresInSeconds = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
				result.IdToken = root.TryGetProperty("id_token", out var it) ? (it.GetString() ?? "") : "";
			}
			catch (Exception ex)
			{
				result.Error = "transport";
				result.ErrorDescription = ex.Message;
				Common.writeToLog("ERROR GoogleOAuthFlow.ExchangeCodeAsync:", ex);
			}
			return result;
		}

		public class IdClaims
		{
			public string Subject { get; set; } = string.Empty;   // sub — stable account id
			public string Email { get; set; } = string.Empty;
		}

		public static IdClaims ParseIdToken(string idToken)
		{
			var claims = new IdClaims();
			try
			{
				var parts = (idToken ?? string.Empty).Split('.');
				if (parts.Length < 2) return claims;
				string payload = parts[1].Replace('-', '+').Replace('_', '/');
				payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
				using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
				var root = doc.RootElement;
				claims.Subject = root.TryGetProperty("sub", out var s) ? (s.GetString() ?? "") : "";
				claims.Email = root.TryGetProperty("email", out var e) ? (e.GetString() ?? "") : "";
			}
			catch (Exception ex) { Common.writeToLog("ERROR GoogleOAuthFlow.ParseIdToken:", ex); }
			return claims;
		}

		// Best-effort revoke on disconnect (kills the refresh token + its grants).
		public static async Task<bool> RevokeAsync(string token)
		{
			if (string.IsNullOrWhiteSpace(token)) return false;
			try
			{
				using var content = new FormUrlEncodedContent(new Dictionary<string, string> { { "token", token } });
				using var resp = await _http.PostAsync("https://oauth2.googleapis.com/revoke", content);
				return resp.IsSuccessStatusCode;
			}
			catch (Exception ex) { Common.writeToLog("WARN GoogleOAuthFlow.RevokeAsync:", ex); return false; }
		}

		// Runtime credential for calendar API calls: stored refresh token → SDK
		// UserCredential (auto-refresh) persisting through OAuthTokenStore.
		public static UserCredential BuildUserCredential(OAuthAccount account)
		{
			var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
			{
				ClientSecrets = new ClientSecrets
				{
					ClientId = Settings.GoogleOAuthClientId,
					ClientSecret = Settings.EffectiveGoogleOAuthClientSecret
				},
				Scopes = new[] { "https://www.googleapis.com/auth/calendar.events",
								 "https://www.googleapis.com/auth/calendar.calendarlist.readonly" },
				DataStore = new OAuthTokenStore(account.oauthAccountID)
			});

			var token = new TokenResponse
			{
				RefreshToken = account.decryptRefreshToken(),
				AccessToken = string.IsNullOrEmpty(account.accessTokenEnc) ? null : account.decryptAccessToken(),
				ExpiresInSeconds = account.accessTokenExpiresAt.HasValue
					? (long)Math.Max(0, (account.accessTokenExpiresAt.Value - DateTime.UtcNow).TotalSeconds)
					: 0,
				IssuedUtc = DateTime.UtcNow
			};
			return new UserCredential(flow, account.oauthAccountID.ToString(), token);
		}
	}
}
