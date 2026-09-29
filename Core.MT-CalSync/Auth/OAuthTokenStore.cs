using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace Core.MTCalSync
{
	// Google SDK IDataStore that persists UserCredential tokens, encrypted, to
	// oauth_account, so later worker processes reuse the access token instead of
	// refreshing every cycle.
	public class OAuthTokenStore : IDataStore
	{
		private readonly long _accountId;

		public OAuthTokenStore(long oauthAccountId)
		{
			_accountId = oauthAccountId;
		}

		public Task StoreAsync<T>(string key, T value)
		{
			try
			{
				if (value is TokenResponse token)
				{
					var expiresAt = token.ExpiresInSeconds.HasValue
						? (token.IssuedUtc == default ? DateTime.UtcNow : token.IssuedUtc).AddSeconds(token.ExpiresInSeconds.Value)
						: DateTime.UtcNow.AddMinutes(50);
					new OAuthAccount().updateTokens(_accountId,
						newRefreshTokenEnc: string.IsNullOrEmpty(token.RefreshToken) ? null : Encryption.Encrypt(token.RefreshToken),
						newAccessTokenEnc: string.IsNullOrEmpty(token.AccessToken) ? null : Encryption.Encrypt(token.AccessToken),
						expiresAt: expiresAt);
				}
			}
			catch (Exception ex) { Common.writeToLog("ERROR OAuthTokenStore.StoreAsync:", ex); }
			return Task.CompletedTask;
		}

		public Task<T> GetAsync<T>(string key)
		{
			try
			{
				var account = new OAuthAccount().getById(_accountId);
				if (account.oauthAccountID > 0 && typeof(T) == typeof(TokenResponse))
				{
					var token = new TokenResponse
					{
						RefreshToken = account.decryptRefreshToken(),
						AccessToken = string.IsNullOrEmpty(account.accessTokenEnc) ? null : account.decryptAccessToken(),
						ExpiresInSeconds = account.accessTokenExpiresAt.HasValue
							? (long)Math.Max(0, (account.accessTokenExpiresAt.Value - DateTime.UtcNow).TotalSeconds)
							: 0,
						IssuedUtc = DateTime.UtcNow
					};
					// Round-trip through the SDK serializer to return a T.
					return Task.FromResult(NewtonsoftJsonSerializer.Instance.Deserialize<T>(
						NewtonsoftJsonSerializer.Instance.Serialize(token)));
				}
			}
			catch (Exception ex) { Common.writeToLog("ERROR OAuthTokenStore.GetAsync:", ex); }
			return Task.FromResult(default(T)!);
		}

		public Task DeleteAsync<T>(string key) => Task.CompletedTask;   // deletion happens via disconnect
		public Task ClearAsync() => Task.CompletedTask;
	}
}
