namespace Core.MTCalSync
{
	// An OAuth account's stored credential no longer works. This is a user problem, not
	// a sync fault: the run ends as `skipped_auth` with no dead-letter, retry or admin
	// alert, and the owner is asked to reconnect. Thrown by ProviderFactory for a
	// flagged account and by the token layers on invalid_grant.
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

		// Rethrows the reauth signal if `ex` is or wraps one. Provider error translation
		// calls this first so the signal reaches the engine instead of becoming a
		// retryable provider error.
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
