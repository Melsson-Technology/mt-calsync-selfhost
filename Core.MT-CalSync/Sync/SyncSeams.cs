namespace Core.MTCalSync
{
	// Extension seams. Two decisions sit outside the engine: whether an owner's pairs may
	// sync now, and how an owner hears about a dead grant or a pair that keeps failing.
	// Each is an interface with a permissive default that hosts embedding the engine can
	// replace at startup. The defaults allow every pair and send plain SMTP alerts to the
	// operator.

	// Whether this owner's pairs may sync now. The default always allows.
	public interface ISyncGate
	{
		bool CanSync(long customerId);
	}

	public sealed class AllowAllSyncGate : ISyncGate
	{
		public bool CanSync(long customerId) => true;
	}

	public static class SyncGate
	{
		// Hosts that embed the engine can replace this at startup.
		public static ISyncGate Current { get; set; } = new AllowAllSyncGate();
	}

	// How a pair's or account's owner is notified. The engine decides when (the 24h reauth
	// dedupe, the failure threshold and per-pair throttle); the implementation decides who
	// receives it and how.
	public interface IOwnerNotifier
	{
		// A grant was revoked or expired. Returns true only if a notification went out, so the
		// engine stamps its 24h dedupe only then.
		bool ReauthNeeded(OAuthAccount account, string provider);

		// A pair keeps failing. The engine has already applied the 3-failure threshold and
		// claimed the per-pair 24h throttle.
		void PersistentFailure(SyncPair pair, SyncRun run);
	}

	// The default notifier: a plain alert to Settings.AlertTo, since on a standalone install
	// the operator is the owner.
	public sealed class SmtpOwnerNotifier : IOwnerNotifier
	{
		public bool ReauthNeeded(OAuthAccount account, string provider)
		{
			string name = provider == Providers.Google ? "Google" : "Microsoft";
			return Email.SendAlert(
				$"{Settings.AppName}: reconnect your {name} account",
				$"Syncing for the {name} account {account.principalEmail} is paused because its access " +
				"expired or was revoked. Reconnect it from the portal to resume. Calendars and settings are unchanged.");
		}

		public void PersistentFailure(SyncPair pair, SyncRun run)
		{
			string err = run.errorText ?? string.Empty;
			if (err.Length > 300) err = err.Substring(0, 300);
			Email.SendAlert(
				$"{Settings.AppName}: sync pair \"{pair.name}\" needs attention",
				$"Sync pair \"{pair.name}\" has been failing repeatedly (latest error: {err}). " +
				"Retries continue with increasing spacing; if it persists, pause and resume the pair.");
		}
	}

	public static class OwnerNotifier
	{
		// Hosts that embed the engine can replace this at startup.
		public static IOwnerNotifier Current { get; set; } = new SmtpOwnerNotifier();
	}
}
