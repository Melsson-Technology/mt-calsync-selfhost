using Core.MTCalSync;

namespace SelfHost.MTCalSync.Models
{
	// Data for the self-serve pair-creation wizard: the user's connected accounts
	// with their live calendar lists, or the reason the wizard is unavailable.
	public class PairWizardModel
	{
		public string BlockReason { get; set; } = string.Empty;
		public bool CanCreate => string.IsNullOrEmpty(BlockReason);
		public List<AccountCalendars> Accounts { get; } = new();

		public class AccountCalendars
		{
			public OAuthAccount Account { get; set; } = new();
			public List<RemoteCalendar> Calendars { get; set; } = new();
			public string Error { get; set; } = string.Empty;
		}
	}
}
