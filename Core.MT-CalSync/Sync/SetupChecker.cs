namespace Core.MTCalSync
{
	// End-to-end validation of everything a sync needs, run before going live.
	// Proves: DB connectivity + schema, that credentials are present, a live read of
	// every calendar that belongs to a pair, and SMTP config presence. Credentials are
	// only exercised through those reads, so with no pairs nothing leaves the box.
	public class SetupChecker
	{
		private readonly List<string> _lines = new();
		private bool _ok = true;

		// The report lines (also surfaced in the Portal). Populated by Run().
		public List<string> Lines => _lines;
		public bool Ok => _ok;

		// How many calendars were actually read. Zero means the PASS covers the database
		// and settings only: a fresh install passes without any provider being contacted,
		// and saying just "PASS" there reads as "your Google and Microsoft setup works".
		public int CalendarsChecked { get; private set; }

		// Runs all checks, populates Lines, returns pass/fail. Does NOT print — callers
		// (the CLI, the Portal) render Lines themselves.
		public bool Run()
		{
			Line("MT-CalSync setup-check");
			Line("======================");
			CheckDb();
			CheckCredPresence();
			CheckPairs().GetAwaiter().GetResult();
			CheckSmtp();
			Line("");
			if (!_ok) Line("RESULT: FAIL. Fix the items marked [FAIL] above.");
			else if (CalendarsChecked == 0) Line("RESULT: PASS for the database and settings only. No calendar was contacted, because no sync pair exists yet.");
			else Line("RESULT: PASS");
			return _ok;
		}

		private void CheckDb()
		{
			try
			{
				var oDA = new DataAccess();
				object? v = oDA.execScalar("select count(*) from sync_pair", new Dictionary<string, object>());
				if (!string.IsNullOrEmpty(oDA.errorMessage)) { Fail("DB", oDA.errorMessage); return; }
				Pass("DB", $"connected; sync_pair rows = {Common.ToInt(v)}");
			}
			catch (Exception ex) { Fail("DB", ex.Message + " (is the schema loaded? run load-schema.sh)"); }
		}

		private void CheckCredPresence()
		{
			// App credentials back app_default (operator) connections only; on a
			// delegated-only install their absence is informational, not a failure.
			bool anyAppDefault = new ProviderConnection().listAll().Any(c => c.authKind == AuthKinds.AppDefault);

			bool graphOk = !string.IsNullOrWhiteSpace(Settings.GraphTenantId) && !string.IsNullOrWhiteSpace(Settings.GraphClientId) && !string.IsNullOrWhiteSpace(Settings.EffectiveGraphClientSecret);
			if (graphOk) Pass("Graph app creds", "tenant/client/secret present");
			else if (anyAppDefault) Fail("Graph app creds", "GraphTenantId/GraphClientId/GraphClientSecret not all set, and app-credential pairs exist");
			else Warn("Graph app creds", "not set. That's fine unless you use app-credential pairs.");

			bool googleOk = !string.IsNullOrWhiteSpace(Settings.EffectiveGoogleServiceAccountJson);
			if (googleOk) Pass("Google app creds", "service-account JSON present");
			else if (anyAppDefault) Fail("Google app creds", "GoogleServiceAccountJsonPath (or inline JSON) not set or not readable, and app-credential pairs exist");
			else Warn("Google app creds", "not set. That's fine unless you use app-credential pairs.");

			// Delegated OAuth clients back the self-serve connect flows.
			if (!string.IsNullOrWhiteSpace(Settings.MsOAuthClientId) && !string.IsNullOrWhiteSpace(Settings.EffectiveMsOAuthClientSecret))
				Pass("MS OAuth client", "client id/secret present");
			else Warn("MS OAuth client", "MsOAuthClientId/MsOAuthClientSecret not set, so Microsoft accounts can't be connected");

			if (!string.IsNullOrWhiteSpace(Settings.GoogleOAuthClientId) && !string.IsNullOrWhiteSpace(Settings.EffectiveGoogleOAuthClientSecret))
				Pass("Google OAuth client", "client id/secret present");
			else Warn("Google OAuth client", "GoogleOAuthClientId/GoogleOAuthClientSecret not set, so Google accounts can't be connected");
		}

		private async Task CheckPairs()
		{
			var pairs = new SyncPair().listAll();
			if (pairs.Count == 0)
			{
				// The portal's pair wizard is the path most installs take; add-pair only
				// makes app-credential pairs, so leading with it sent people the wrong way.
				Warn("Pairs", "no sync pairs yet, so no calendar was contacted. Create one on the Pairs page " +
					"(or with `add-pair` for an app-credential pair), then run this again.");
				return;
			}
			foreach (var pair in pairs)
			{
				var connL = new ProviderConnection().getById(pair.leftConnectionID);
				var connR = new ProviderConnection().getById(pair.rightConnectionID);
				await Probe(pair, connL);
				await Probe(pair, connR);
			}
		}

		private async Task Probe(SyncPair pair, ProviderConnection conn)
		{
			string label = $"{conn.provider}:{conn.principalEmail}";
			CalendarsChecked++;
			try
			{
				var provider = ProviderFactory.Create(conn);
				var window = RollingWindow.Around(DateTime.UtcNow, 1, 2);
				var cs = await provider.GetChangesAsync(window, new SyncState { pairID = pair.pairID, provider = conn.provider }, true);
				Pass("Calendar " + label, $"live read OK ({cs.Items.Count} events in a 3-day probe window)");
			}
			catch (NeedsReauthException)
			{
				// A user's grant needing reconnect isn't an install problem.
				Warn("Calendar " + label, "owner's OAuth grant needs reconnect (pair skips until they do)");
			}
			catch (Exception ex)
			{
				Fail("Calendar " + label, ex.Message);
			}
		}

		private void CheckSmtp()
		{
			if (string.IsNullOrWhiteSpace(Settings.SmtpHost) || string.IsNullOrWhiteSpace(Settings.AlertTo))
				Warn("SMTP", "SmtpHost/AlertTo not set, so failure alerts are off.");
			else Pass("SMTP", $"host={Settings.SmtpHost}:{Settings.SmtpPort} alertTo={Settings.AlertTo} (send a test with `test-email`)");
		}

		private void Pass(string area, string msg) => Line($"[ OK ] {area}: {msg}");
		private void Warn(string area, string msg) => Line($"[WARN] {area}: {msg}");
		private void Fail(string area, string msg) { _ok = false; Line($"[FAIL] {area}: {msg}"); }
		private void Line(string s) => _lines.Add(s);
	}
}
