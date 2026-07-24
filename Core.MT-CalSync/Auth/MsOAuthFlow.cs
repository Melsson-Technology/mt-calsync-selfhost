using System.Text;
using System.Text.Json;

namespace Core.MTCalSync
{
	// Microsoft identity platform (v2) authorization-code + refresh-token flow,
	// spoken directly over HTTP. Deliberately not MSAL: its serialized token cache
	// doesn't fit the one-encrypted-column-per-account model, and the two POSTs we
	// need are stable, documented endpoints. The multitenant app authorizes against
	// /organizations; refresh MUST target the account's home tenant (captured from
	// the id_token at connect) — refreshing against `common` fails for guests.
	public static class MsOAuthFlow
	{
		private static readonly HttpClient _http = new();

		// Delegated scopes. Reserved OIDC scopes ride alongside the fully-qualified
		// Graph resource scopes; offline_access is what yields the refresh token.
		public const string Scopes =
			"openid profile email offline_access " +
			"https://graph.microsoft.com/Calendars.ReadWrite " +
			"https://graph.microsoft.com/Calendars.ReadWrite.Shared " +
			"https://graph.microsoft.com/User.Read";

		public class TokenResult
		{
			public string AccessToken { get; set; } = string.Empty;
			public string RefreshToken { get; set; } = string.Empty;
			public int ExpiresInSeconds { get; set; }
			public string IdToken { get; set; } = string.Empty;
			public string Error { get; set; } = string.Empty;          // OAuth error code when failed
			public string ErrorDescription { get; set; } = string.Empty;
			public bool Ok => string.IsNullOrEmpty(Error) && !string.IsNullOrEmpty(AccessToken);
			public bool IsInvalidGrant => Error == "invalid_grant";
		}

		public static string BuildAuthorizeUrl(string state, string codeChallenge, string redirectUri)
		{
			var q = new Dictionary<string, string>
			{
				{ "client_id", Settings.MsOAuthClientId },
				{ "response_type", "code" },
				{ "redirect_uri", redirectUri },
				{ "response_mode", "query" },
				{ "scope", Scopes },
				{ "state", state },
				{ "code_challenge", codeChallenge },
				{ "code_challenge_method", "S256" },
				{ "prompt", "select_account" }
			};
			return "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize?" + Encode(q);
		}

		public static Task<TokenResult> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri) =>
			PostTokenAsync("organizations", new Dictionary<string, string>
			{
				{ "client_id", Settings.MsOAuthClientId },
				{ "client_secret", Settings.EffectiveMsOAuthClientSecret },
				{ "grant_type", "authorization_code" },
				{ "code", code },
				{ "redirect_uri", redirectUri },
				{ "code_verifier", codeVerifier },
				{ "scope", Scopes }
			});

		public static Task<TokenResult> RefreshAsync(string homeTenantId, string refreshToken) =>
			PostTokenAsync(string.IsNullOrWhiteSpace(homeTenantId) ? "organizations" : homeTenantId,
				new Dictionary<string, string>
				{
					{ "client_id", Settings.MsOAuthClientId },
					{ "client_secret", Settings.EffectiveMsOAuthClientSecret },
					{ "grant_type", "refresh_token" },
					{ "refresh_token", refreshToken },
					{ "scope", Scopes }
				});

		private static async Task<TokenResult> PostTokenAsync(string tenant, Dictionary<string, string> form)
		{
			var result = new TokenResult();
			try
			{
				using var content = new FormUrlEncodedContent(form);
				using var resp = await _http.PostAsync($"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token", content);
				string body = await resp.Content.ReadAsStringAsync();
				using var doc = JsonDocument.Parse(body);
				var root = doc.RootElement;

				if (root.TryGetProperty("error", out var err))
				{
					result.Error = err.GetString() ?? "unknown";
					result.ErrorDescription = root.TryGetProperty("error_description", out var ed) ? (ed.GetString() ?? "") : "";
					// Never log tokens; the error description is safe and diagnostic.
					Common.writeToLog($"MS token endpoint error ({result.Error}): {Truncate(result.ErrorDescription, 300)}");
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
				Common.writeToLog("ERROR MsOAuthFlow.PostTokenAsync:", ex);
			}
			return result;
		}

		// Claims we need from the id_token. The token arrived directly from the
		// token endpoint over TLS, so decoding without signature validation is fine.
		public class IdClaims
		{
			public string ObjectId { get; set; } = string.Empty;      // oid — immutable account id
			public string TenantId { get; set; } = string.Empty;      // tid — home tenant
			public string Email { get; set; } = string.Empty;
			public string Name { get; set; } = string.Empty;
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
				claims.ObjectId = Str(root, "oid");
				claims.TenantId = Str(root, "tid");
				claims.Name = Str(root, "name");
				claims.Email = Str(root, "email");
				if (string.IsNullOrEmpty(claims.Email)) claims.Email = Str(root, "preferred_username");
			}
			catch (Exception ex) { Common.writeToLog("ERROR MsOAuthFlow.ParseIdToken:", ex); }
			return claims;
		}

		private static string Str(JsonElement root, string name) =>
			root.TryGetProperty(name, out var v) ? (v.GetString() ?? string.Empty) : string.Empty;

		private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

		private static string Encode(Dictionary<string, string> q) =>
			string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
	}
}
