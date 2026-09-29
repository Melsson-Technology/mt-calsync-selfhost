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

		// Rethrows the reauth signal if `ex` is one or wraps one. A provider's error
		// translation calls this first: the token layer raises it from inside a provider
		// call, and translated into a retryable provider error it never reached the
		// engine, so the owner was never asked to reconnect.
		public static void ThrowIfWrapped(Exception ex)
		{
			for (Exception? e = ex; e != null; e = e.InnerException)
				if (e is NeedsReauthException nre)
					System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(nre).Throw();
			if (ex is AggregateException agg)
				foreach (var inner in agg.InnerExceptions) ThrowIfWrapped(inner);
		}
	}
}
