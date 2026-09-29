using Core.MTCalSync;

namespace SelfHost.MTCalSync.Models
{
	// The Dashboard: setup progress plus the accounts and pairs cards. The flags make no
	// live provider calls, so the page renders in one controller pass.
	public class DashboardViewModel
	{
		public bool IsAdmin { get; set; }

		public bool GoogleConnected { get; set; }     // any google account, isConnected
		public bool MicrosoftConnected { get; set; }  // any m365 account,  isConnected
		public bool HasPairs { get; set; }

		public List<OAuthAccount> Accounts { get; set; } = new();
		public List<PairRow> Pairs { get; set; } = new();

		public List<OAuthAccount> ReauthAccounts => Accounts.Where(a => !a.isConnected).ToList();
		public bool BothProvidersConnected => GoogleConnected && MicrosoftConnected;

		// Shown only while there is no pair. A later needs_reauth doesn't bring the guide
		// back; the reconnect banner covers that.
		public bool ShowSetupGuide => !HasPairs;
	}

	public class PairRow
	{
		public long PairID { get; set; }
		public string Name { get; set; } = string.Empty;
		public string Direction { get; set; } = string.Empty;
		public string Fidelity { get; set; } = string.Empty;
		public string Recurrence { get; set; } = string.Empty;
		public bool Enabled { get; set; }
		public string M365Email { get; set; } = string.Empty;
		public string GoogleEmail { get; set; } = string.Empty;
		public bool M365Token { get; set; }
		public bool GoogleToken { get; set; }
		public string LastSuccess { get; set; } = "never";
		public int Mappings { get; set; }
		public int OpenDeadLetters { get; set; }
	}

	public class HomeViewModel
	{
		public bool DbOk { get; set; }
		public string DbMessage { get; set; } = string.Empty;
		public bool MsOAuthConfigured { get; set; }       // sign-in client for Connect Microsoft
		public bool GoogleOAuthConfigured { get; set; }   // sign-in client for Connect Google
		public bool GraphConfigured { get; set; }         // app-only (app-credential pairs)
		public bool GoogleConfigured { get; set; }        // service account (app-credential pairs)
		public bool SmtpConfigured { get; set; }
		public List<PairRow> Pairs { get; set; } = new();
		public List<string>? TestReport { get; set; }
		public bool? TestOk { get; set; }
		public int TestCalendars { get; set; }            // calendars the last test read
	}
}
