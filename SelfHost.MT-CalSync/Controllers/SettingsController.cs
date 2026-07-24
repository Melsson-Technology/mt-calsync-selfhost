using Core.MTCalSync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfHost.MTCalSync.Models;

namespace SelfHost.MTCalSync.Controllers
{
	// /Settings — the credentials + config form. Secrets (Graph client secret, Google
	// service-account JSON, SMTP password) are stored AES-encrypted in the DB settings
	// table and never rendered back to the page (only a masked status). Plain config is
	// stored plaintext and resolves DB-first, so the UI is the source of truth.
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

			// Secrets — write-only. Only overwrite when a non-blank value is submitted, so a
			// blank field preserves the stored secret. Stored encrypted.
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

			TempData["Info"] = "Settings saved. Use the dashboard's “Test credentials” to validate live access.";
			return RedirectToAction("Index");
		}

		private static string Trim(string? v) => (v ?? string.Empty).Trim();

		private static string Mask(string v)
		{
			if (string.IsNullOrWhiteSpace(v)) return "not set";
			string last4 = v.Length >= 4 ? v.Substring(v.Length - 4) : v;
			return "configured — ••••" + last4;
		}

		private static string JsonStatus(string v) =>
			string.IsNullOrWhiteSpace(v) ? "not set" : $"configured — {v.Length} chars";
	}
}
