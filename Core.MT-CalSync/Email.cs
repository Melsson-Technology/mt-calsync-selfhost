using System.Net;
using System.Net.Mail;

namespace Core.MTCalSync
{
	// Minimal SMTP sender for alerts + transactional mail. Uses System.Net.Mail
	// with the SMTP config from settings — no third-party email dependency.
	// Never throws: a broken mailer must not fail a sync run.
	public class Email : @base
	{
		public string sendTo { get; set; } = string.Empty;
		public string subject { get; set; } = string.Empty;
		public string bodyText { get; set; } = string.Empty;
		public bool isHtml { get; set; }

		public bool sendEmail()
		{
			string host = Settings.SmtpHost;
			string to = string.IsNullOrWhiteSpace(sendTo) ? Settings.AlertTo : sendTo;
			string from = string.IsNullOrWhiteSpace(Settings.SmtpFrom) ? Settings.SmtpUser : Settings.SmtpFrom;

			if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(from))
			{
				Common.writeToLog($"Email skipped — SMTP not fully configured (host='{host}', to='{to}', from='{from}'). Subject: {subject}");
				return false;
			}

			try
			{
				using var mailMsg = new MailMessage { From = new MailAddress(from) };
				foreach (var addr in to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
					mailMsg.To.Add(new MailAddress(addr));
				mailMsg.Subject = subject.Trim();
				mailMsg.Body = bodyText;
				mailMsg.IsBodyHtml = isHtml;

				using var smtp = new SmtpClient(host, Settings.SmtpPort) { EnableSsl = Settings.SmtpUseSsl };
				string user = Settings.SmtpUser;
				string pass = Settings.EffectiveSmtpPassword;
				if (!string.IsNullOrWhiteSpace(user))
					smtp.Credentials = new NetworkCredential(user, pass);
				smtp.Send(mailMsg);
				Common.writeToLog($"Alert email sent to {to}: {subject}");
				return true;
			}
			catch (Exception ex)
			{
				errorMessage = ex.Message;
				Common.writeToLog("ERROR in Email.sendEmail(): ", ex);
				return false;
			}
		}

		// Convenience: fire a one-off operator alert (goes to Settings.AlertTo).
		public static bool SendAlert(string subject, string body)
		{
			var e = new Email { subject = subject, bodyText = body };
			return e.sendEmail();
		}

		// User-facing notification to a specific address.
		public static bool SendUserAlert(string toEmail, string subject, string body)
		{
			var e = new Email { sendTo = toEmail, subject = subject, bodyText = body };
			return e.sendEmail();
		}
	}
}
