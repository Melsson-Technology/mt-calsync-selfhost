using Azure.Core;

namespace Core.MTCalSync
{
	// Azure TokenCredential backed by a stored refresh token — what GraphServiceClient
	// uses for delegated (per-user OAuth) connections. Token custody:
	//   1. in-memory cache (this process),
	//   2. encrypted access token in oauth_account (worker runs are separate
	//      short-lived processes; a ~60-min access token spans many 5-min cycles),
	//   3. refresh-token POST to the account's HOME tenant, persisting the rotated
	//      refresh token whenever Microsoft returns one.
	// invalid_grant means the grant is dead (revoked/expired/policy) → mark the
	// account needs_reauth and surface NeedsReauthException so the engine files the
	// run as skipped_auth instead of dead-lettering.
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

				// Reload the row each time — another process may have rotated the
				// refresh token or cached a newer access token since we were built.
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
					// Transient endpoint trouble — fail this run without flagging the account.
					throw new ProviderException($"ms.token: {res.Error}: {res.ErrorDescription}", res.Error, isTransient: true);
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
