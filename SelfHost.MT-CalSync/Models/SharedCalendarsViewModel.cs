namespace SelfHost.MTCalSync.Models
{
	// One shared Google calendar the picker can toggle.
	public class SharedCalendarRow
	{
		public string CalendarId { get; set; } = string.Empty;
		public string Summary { get; set; } = string.Empty;
		public string AccessRole { get; set; } = string.Empty;
		public bool IsSynced { get; set; }   // a mirror pair exists for this calendar
		public bool Enabled { get; set; }     // …and it's active (not paused)
		public long PairId { get; set; }
		public int Mappings { get; set; }
	}

	public class SharedCalendarsViewModel
	{
		public bool Ready { get; set; }                 // connections resolved + list fetched
		public string? Error { get; set; }              // why we couldn't build the list
		public string M365Email { get; set; } = string.Empty;    // destination mailbox (primary cal)
		public string GoogleEmail { get; set; } = string.Empty;  // impersonated source account
		public List<SharedCalendarRow> Calendars { get; set; } = new();
	}
}
