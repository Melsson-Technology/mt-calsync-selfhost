using Azure.Core;

namespace Core.MTCalSync
{
	// TokenCredential for delegated Graph connections, backed by a stored refresh token.
	// Lookup order: in-process cache; the encrypted access token in oauth_account (it
	// outlives short-lived worker processes); a refresh against the home tenant, saving
	// any rotated refresh token. invalid_grant marks the account needs_reauth and raises
	// NeedsReauthException, so the run is skipped rather than dead-lettered.
	public class MsDelegatedTokenCredential : TokenCredential
	{
		private readonly long _accountId;
		private readonly SemaphoreSlim _gate = new(1, 1);
		private string? _cachedToken;
		private DateTimeOffset _cachedExpiry;

		public MsDelegatedTokenCredential(long oauthAccountId)
		{
			_accountId = oauthAccountId;
		}

		public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
			GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

		public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
		{
			if (Fresh()) return new AccessToken(_cachedToken!, _cachedExpiry);

			await _gate.WaitAsync(cancellationToken);
			try
			{
				if (Fresh()) return new AccessToken(_cachedToken!, _cachedExpiry);

				// Reload the row: another process may have rotated the refresh token or
				// stored a newer access token.
				var account = new OAuthAccount().getById(_accountId);
				if (account.oauthAccountID == 0 || !account.isConnected)
					throw new NeedsReauthException(_accountId, Providers.M365, "OAuth account missing or not connected.");

				if (account.accessTokenExpiresAt.HasValue &&
					account.accessTokenExpiresAt.Value > DateTime.UtcNow.AddMinutes(5) &&
					!string.IsNullOrEmpty(account.accessTokenEnc))
				{
					_cachedToken = account.decryptAccessToken();
					_cachedExpiry = new DateTimeOffset(account.accessTokenExpiresAt.Value, TimeSpan.Zero);
					return new AccessToken(_cachedToken, _cachedExpiry);
				}

				string refreshToken = account.decryptRefreshToken();
				if (string.IsNullOrEmpty(refreshToken))
				{
					account.markNeedsReauth(_accountId, "No refresh token stored.");
					throw new NeedsReauthException(_accountId, Providers.M365, "No refresh token stored.");
				}

				var res = await MsOAuthFlow.RefreshAsync(account.tenantId, refreshToken);
				if (!res.Ok)
				{
					if (res.IsInvalidGrant)
					{
						account.markNeedsReauth(_accountId, $"{res.Error}: {res.ErrorDescription}");
						throw new NeedsReauthException(_accountId, Providers.M365, "Refresh token rejected (invalid_grant).");
					}
					// invalid_client/unauthorized_client mean the app's own registration is wrong,
					// which no retry fixes. Anything else is endpoint trouble: fail this run
					// without flagging the account.
					bool appMisconfigured = res.Error == "invalid_client" || res.Error == "unauthorized_client";
					throw new ProviderException($"ms.token: {res.Error}: {res.ErrorDescription}", res.Error, isTransient: !appMisconfigured);
				}

				var expiresAt = DateTime.UtcNow.AddSeconds(Math.Max(60, res.ExpiresInSeconds));
				account.updateTokens(_accountId,
					newRefreshTokenEnc: string.IsNullOrEmpty(res.RefreshToken) ? null : Encryption.Encrypt(res.RefreshToken),
					newAccessTokenEnc: Encryption.Encrypt(res.AccessToken),
					expiresAt: expiresAt);

				_cachedToken = res.AccessToken;
				_cachedExpiry = new DateTimeOffset(expiresAt, TimeSpan.Zero);
				return new AccessToken(_cachedToken, _cachedExpiry);
			}
			finally { _gate.Release(); }
		}

		private bool Fresh() => _cachedToken != null && _cachedExpiry > DateTimeOffset.UtcNow.AddMinutes(5);
	}
}
