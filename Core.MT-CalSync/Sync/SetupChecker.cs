namespace Core.MTCalSync
{
	// End-to-end validation of everything a sync needs, run before going live.
	// Proves: DB connectivity + schema, both provider credentials + calendar access
	// (a live read per configured connection), and SMTP config presence.
	public class SetupChecker
	{
		private readonly List<string> _lines = new();
		private bool _ok = true;

		// The report lines (also surfaced in the Portal). Populated by Run().
		public List<string> Lines => _lines;
		public bool Ok => _ok;

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
			Line(_ok ? "RESULT: PASS" : "RESULT: FAIL — fix the items marked [FAIL] above.");
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
			else if (anyAppDefault) Fail("Graph app creds", "GraphTenantId/GraphClientId/GraphClientSecret not all set (app_default connections exist)");
			else Warn("Graph app creds", "not set — fine unless operator (app_default) connections are used");

			bool googleOk = !string.IsNullOrWhiteSpace(Settings.EffectiveGoogleServiceAccountJson);
			if (googleOk) Pass("Google app creds", "service-account JSON present");
			else if (anyAppDefault) Fail("Google app creds", "GoogleServiceAccountJsonPath (or inline JSON) not set/readable (app_default connections exist)");
			else Warn("Google app creds", "not set — fine unless operator (app_default) connections are used");

			// Delegated OAuth clients back the self-serve connect flows.
			if (!string.IsNullOrWhiteSpace(Settings.MsOAuthClientId) && !string.IsNullOrWhiteSpace(Settings.EffectiveMsOAuthClientSecret))
				Pass("MS OAuth client", "client id/secret present");
			else Warn("MS OAuth client", "MsOAuthClientId/MsOAuthClientSecret not set — users can't connect Microsoft accounts");

			if (!string.IsNullOrWhiteSpace(Settings.GoogleOAuthClientId) && !string.IsNullOrWhiteSpace(Settings.EffectiveGoogleOAuthClientSecret))
				Pass("Google OAuth client", "client id/secret present");
			else Warn("Google OAuth client", "GoogleOAuthClientId/GoogleOAuthClientSecret not set — users can't connect Google accounts");
		}

		private async Task CheckPairs()
		{
			var pairs = new SyncPair().listAll();
			if (pairs.Count == 0)
			{
				Warn("Pairs", "no sync pairs configured — run `add-pair`, then re-run setup-check to test live calendar access.");
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
				Warn("SMTP", "SmtpHost/AlertTo not set — failure alerts are disabled.");
			else Pass("SMTP", $"host={Settings.SmtpHost}:{Settings.SmtpPort} alertTo={Settings.AlertTo} (send a test with `test-email`)");
		}

		private void Pass(string area, string msg) => Line($"[ OK ] {area}: {msg}");
		private void Warn(string area, string msg) => Line($"[WARN] {area}: {msg}");
		private void Fail(string area, string msg) { _ok = false; Line($"[FAIL] {area}: {msg}"); }
		private void Line(string s) => _lines.Add(s);
	}
}
