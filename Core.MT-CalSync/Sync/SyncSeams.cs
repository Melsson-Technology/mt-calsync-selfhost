namespace Core.MTCalSync
{
	// ── Extension seams: where the engine defers subscription/identity policy ────
	//
	// The sync engine is self-contained and runs standalone (self-host). Two
	// behaviors are NOT the engine's to decide, so each is expressed as an interface
	// with a permissive default that a hosting layer can replace at startup
	// (installed by the hosting layer at startup): (1) whether a customer may sync now,
	// and (2) how the owner of a pair/account is told about a dead grant or a pair
	// that keeps failing.
	//
	// Defaults = self-host: always eligible, and notifications go to the operator
	// alert address as plain SMTP — no user table, no templates, no dedupe ledger.

	// "Is this customer allowed to sync right now?" Default: always yes.
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
		// Replaced by the hosting layer at startup. Defaults to allow-all
		// so a standalone engine never gates on a subscription it doesn't have.
		public static ISyncGate Current { get; set; } = new AllowAllSyncGate();
	}

	// How the owner of a pair/account is notified. The engine decides WHEN (the 24h
	// reauth dedupe, the consecutive-failure threshold + per-pair throttle); the
	// implementation decides WHO (which recipient) and HOW (plain alert vs template).
	public interface IOwnerNotifier
	{
		// A grant died (revoked/expired). Return true iff a notification was actually
		// issued, so the engine stamps its 24h dedupe only when one fired.
		bool ReauthNeeded(OAuthAccount account, string provider);

		// A pair has failed persistently. The engine has already applied the
		// >=3-consecutive-failure threshold and claimed the per-pair 24h throttle.
		void PersistentFailure(SyncPair pair, SyncRun run);
	}

	// Default notifier for self-host: a plain operator alert (to Settings.AlertTo).
	// The self-host operator IS the owner and just needs to know something needs a hand.
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
		// Replaced by the hosting layer at startup. Defaults to operator SMTP.
		public static IOwnerNotifier Current { get; set; } = new SmtpOwnerNotifier();
	}
}
