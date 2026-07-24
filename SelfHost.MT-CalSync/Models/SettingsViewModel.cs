namespace SelfHost.MTCalSync.Models
{
	// Backing model for the credentials/config form. Secrets are never sent back to the
	// page as values — only a masked "…status". Plain config is prefilled.
	public class SettingsViewModel
	{
		// Microsoft 365 / Graph
		public string GraphTenantId { get; set; } = string.Empty;
		public string GraphClientId { get; set; } = string.Empty;
		public string GraphClientSecretStatus { get; set; } = "not set";

		// Google Workspace
		public string GoogleServiceAccountJsonPath { get; set; } = string.Empty;
		public string GoogleJsonStatus { get; set; } = "not set";

		// SMTP (failure alerts)
		public string SmtpHost { get; set; } = string.Empty;
		public int SmtpPort { get; set; } = 587;
		public bool SmtpUseSsl { get; set; } = true;
		public string SmtpUser { get; set; } = string.Empty;
		public string SmtpPasswordStatus { get; set; } = "not set";
		public string SmtpFrom { get; set; } = string.Empty;
		public string AlertTo { get; set; } = string.Empty;

		// Sync defaults
		public int WindowDays { get; set; } = 60;
		public int LookbackDays { get; set; } = 1;
		public string FidelityMode { get; set; } = "full_detail";
		public bool CopyAttendeesToBody { get; set; }
		public int MaxWritesPerRun { get; set; } = 25;
		public int FullResyncHour { get; set; } = 3;

		// Delegated OAuth clients (user connect flows)
		public string MsOAuthClientId { get; set; } = string.Empty;
		public string MsOAuthClientSecretStatus { get; set; } = "not set";
		public string GoogleOAuthClientId { get; set; } = string.Empty;
		public string GoogleOAuthClientSecretStatus { get; set; } = "not set";
		public bool UnverifiedAppNotice { get; set; } = true;

		// Service
		public string PublicBaseUrl { get; set; } = string.Empty;
		public int WorkerMaxConcurrency { get; set; } = 2;
	}
}
