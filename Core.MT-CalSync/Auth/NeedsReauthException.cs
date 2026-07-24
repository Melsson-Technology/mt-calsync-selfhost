namespace Core.MTCalSync
{
	// The stored credential for an OAuth account no longer works (revoked, expired,
	// or consent withdrawn). This is a USER problem, not a sync fault: the engine
	// finishes the run as `skipped_auth` (no dead-letter, no retry storm, no admin
	// alert) and the owner is asked to reconnect. Thrown by ProviderFactory when
	// the account is already flagged, and by the token layers on invalid_grant.
	public class NeedsReauthException : Exception
	{
		public long OAuthAccountId { get; }
		public string Provider { get; }

		public NeedsReauthException(long oauthAccountId, string provider, string message)
			: base(message)
		{
			OAuthAccountId = oauthAccountId;
			Provider = provider;
		}
	}
}
