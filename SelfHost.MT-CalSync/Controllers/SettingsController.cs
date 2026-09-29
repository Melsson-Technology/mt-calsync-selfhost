using Core.MTCalSync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfHost.MTCalSync.Models;

namespace SelfHost.MTCalSync.Controllers
{
	// /Settings: credentials and config. Secrets are stored AES-encrypted in the settings
	// table and never rendered back in full (see Mask). Plain config is stored as plaintext
	// and resolves DB-first, so the UI is the source of truth.
	[Authorize(Roles = "Admin")]
	public class SettingsController : Controller
	{
		[HttpGet]
		public IActionResult Index()
		{
			var vm = new SettingsViewModel
			{
				GraphTenantId = Settings.GraphTenantId,
				GraphClientId = Settings.GraphClientId,
				GraphClientSecretStatus = Mask(Settings.EffectiveGraphClientSecret),
				GoogleServiceAccountJsonPath = Settings.GoogleServiceAccountJsonPath,
				GoogleJsonStatus = JsonStatus(Settings.EffectiveGoogleServiceAccountJson),
				SmtpHost = Settings.SmtpHost,
				SmtpPort = Settings.SmtpPort,
				SmtpUseSsl = Settings.SmtpUseSsl,
				SmtpUser = Settings.SmtpUser,
				SmtpPasswordStatus = Mask(Settings.EffectiveSmtpPassword),
				SmtpFrom = Settings.SmtpFrom,
				AlertTo = Settings.AlertTo,
				WindowDays = Settings.WindowDays,
				LookbackDays = Settings.LookbackDays,
				FidelityMode = Settings.FidelityMode,
				CopyAttendeesToBody = Settings.CopyAttendeesToBody,
				MaxWritesPerRun = Settings.MaxWritesPerRun,
				FullResyncHour = Settings.FullResyncHour,
				MsOAuthClientId = Settings.MsOAuthClientId,
				MsOAuthClientSecretStatus = Mask(Settings.EffectiveMsOAuthClientSecret),
				GoogleOAuthClientId = Settings.GoogleOAuthClientId,
				GoogleOAuthClientSecretStatus = Mask(Settings.EffectiveGoogleOAuthClientSecret),
				UnverifiedAppNotice = Settings.UnverifiedAppNotice,
				PublicBaseUrl = Settings.PublicBaseUrl,
				WorkerMaxConcurrency = Settings.WorkerMaxConcurrency,
			};
			return View(vm);
		}

		[HttpPost, ValidateAntiForgeryToken]
		public IActionResult Save(SettingsViewModel form, string? graphClientSecret, string? googleServiceAccountJson,
			string? smtpPassword, string? msOAuthClientSecret, string? googleOAuthClientSecret)
		{
			var s = new Settings();

			// Plain config (stored plaintext; resolves DB-first).
			s.saveByName("GraphTenantId", Trim(form.GraphTenantId));
			s.saveByName("GraphClientId", Trim(form.GraphClientId));
			s.saveByName("GoogleServiceAccountJsonPath", Trim(form.GoogleServiceAccountJsonPath));
			s.saveByName("SmtpHost", Trim(form.SmtpHost));
			s.saveByName("SmtpPort", form.SmtpPort.ToString());
			s.saveByName("SmtpUseSsl", form.SmtpUseSsl ? "true" : "false");
			s.saveByName("SmtpUser", Trim(form.SmtpUser));
			s.saveByName("SmtpFrom", Trim(form.SmtpFrom));
			s.saveByName("AlertTo", Trim(form.AlertTo));
			s.saveByName("WindowDays", form.WindowDays.ToString());
			s.saveByName("LookbackDays", form.LookbackDays.ToString());
			s.saveByName("FidelityMode", string.IsNullOrWhiteSpace(form.FidelityMode) ? "full_detail" : form.FidelityMode);
			s.saveByName("CopyAttendeesToBody", form.CopyAttendeesToBody ? "true" : "false");
			s.saveByName("MaxWritesPerRun", form.MaxWritesPerRun.ToString());
			s.saveByName("FullResyncHour", form.FullResyncHour.ToString());
			s.saveByName("MsOAuthClientId", Trim(form.MsOAuthClientId));
			s.saveByName("GoogleOAuthClientId", Trim(form.GoogleOAuthClientId));
			s.saveByName("UnverifiedAppNotice", form.UnverifiedAppNotice ? "true" : "false");
			s.saveByName("PublicBaseUrl", Trim(form.PublicBaseUrl).TrimEnd('/'));
			s.saveByName("WorkerMaxConcurrency", form.WorkerMaxConcurrency.ToString());

			// Secrets are write-only: a blank field keeps the stored secret.
			if (!string.IsNullOrWhiteSpace(graphClientSecret))
				s.saveByName("GraphClientSecret", Encryption.Encrypt(graphClientSecret.Trim()));
			if (!string.IsNullOrWhiteSpace(googleServiceAccountJson))
				s.saveByName("GoogleServiceAccountJson", Encryption.Encrypt(googleServiceAccountJson.Trim()));
			if (!string.IsNullOrWhiteSpace(smtpPassword))
				s.saveByName("SmtpPassword", Encryption.Encrypt(smtpPassword.Trim()));
			if (!string.IsNullOrWhiteSpace(msOAuthClientSecret))
				s.saveByName("MsOAuthClientSecret", Encryption.Encrypt(msOAuthClientSecret.Trim()));
			if (!string.IsNullOrWhiteSpace(googleOAuthClientSecret))
				s.saveByName("GoogleOAuthClientSecret", Encryption.Encrypt(googleOAuthClientSecret.Trim()));

			TempData["Info"] = "Settings saved. Use Test credentials on the System page to check them.";
			return RedirectToAction("Index");
		}

		private static string Trim(string? v) => (v ?? string.Empty).Trim();

		// The last four characters identify a long random secret at negligible cost. A short
		// value is usually a chosen password (SMTP), so it shows only that something is set.
		private const int MinLengthToHint = 20;

		private static string Mask(string v)
		{
			if (string.IsNullOrWhiteSpace(v)) return "not set";
			return v.Length >= MinLengthToHint ? "configured: ••••" + v.Substring(v.Length - 4) : "configured";
		}

		private static string JsonStatus(string v) =>
			string.IsNullOrWhiteSpace(v) ? "not set" : $"configured: {v.Length} chars";
	}
}
