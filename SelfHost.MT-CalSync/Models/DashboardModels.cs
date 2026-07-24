using Core.MTCalSync;

namespace SelfHost.MTCalSync.Models
{
	// The signed-in customer's Dashboard: onboarding progress + the accounts/pairs
	// cards. All the onboarding flags are cheap (no live provider calls) so the
	// "Get started" guide can render from a single controller pass.
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

		// First-run artifact: shown only while there is no pair yet. A pair can't exist
		// unless both providers were connected at create time, so !HasPairs is a robust
		// "setup incomplete" proxy — and the guide never resurrects on a later
		// needs_reauth (the reconnect banner covers that).
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
		public bool GraphConfigured { get; set; }
		public bool GoogleConfigured { get; set; }
		public bool SmtpConfigured { get; set; }
		public List<PairRow> Pairs { get; set; } = new();
		public List<string>? TestReport { get; set; }
		public bool? TestOk { get; set; }
	}
}
