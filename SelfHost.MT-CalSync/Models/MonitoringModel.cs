namespace SelfHost.MTCalSync.Models
{
	// Data for the operator Monitoring page.
	public class MonitoringModel
	{
		public string Error { get; set; } = string.Empty;
		public int DuePairs { get; set; }
		public int ReauthAccounts { get; set; }
		public int OpenDeadLetters { get; set; }
		public List<PairHealth> Pairs { get; } = new();

		public class PairHealth
		{
			public long PairID { get; set; }
			public string Name { get; set; } = string.Empty;
			public string Direction { get; set; } = string.Empty;
			public bool Enabled { get; set; }
			public DateTime? NextRunAt { get; set; }
			public string LastStatus { get; set; } = string.Empty;
			public DateTime? LastFinishedAt { get; set; }
			public int OpenDeadLetters { get; set; }
		}
	}
}
