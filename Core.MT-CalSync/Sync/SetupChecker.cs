namespace Core.MTCalSync
{
	// Checks everything a sync needs: the database and schema, credential presence, a live
	// read of every paired calendar, and SMTP settings. Credentials are used only through
	// those reads, so with no pairs nothing leaves the machine.
	public class SetupChecker
	{
		private readonly List<string> _lines = new();
		private bool _ok = true;

		// The report lines, filled by Run() and also shown in the Portal.
		public List<string> Lines => _lines;
		public bool Ok => _ok;

		// How many calendars were read. At zero a PASS covers only the database and settings,
		// and the result line says so rather than implying the providers work.
		public int CalendarsChecked { get; private set; }

		// Runs every check and returns pass or fail. It doesn't print; the CLI and the Portal
		// render Lines themselves.
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
			// App credentials back only app_default (operator) connections, so a delegated-only
			// install warns rather than fails without them.
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
				// Point to the portal's pair wizard first: add-pair only makes app-credential pairs.
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
				// A grant that needs reconnecting isn't an install problem.
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
