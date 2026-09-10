using System.Text;
using Core.MTCalSync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfHost.MTCalSync.Models;

namespace SelfHost.MTCalSync.Controllers
{
	// /Pairs — create and manage sync pairs (calendar links). Every user manages
	// their own pairs, built from their connected accounts' calendars; admins can
	// flip to an all-pairs view. The legacy operator form (app_default connections
	// addressed by raw email) stays admin-only.
	public class PairsController : Controller
	{
		// ── list ──────────────────────────────────────────────────────────────
		[HttpGet]
		public IActionResult Index(bool all = false)
		{
			long userId = PortalAuth.UserId(User);
			bool showAll = all && PortalAuth.IsAdmin(User);
			var pairs = showAll ? new SyncPair().listAll() : new SyncPair().listByUser(userId);

			var rows = new List<PairRow>();
			var em = new EventMapping();
			var dl = new DeadLetter();
			foreach (var p in pairs)
			{
				var l = new ProviderConnection().getById(p.leftConnectionID);
				var r = new ProviderConnection().getById(p.rightConnectionID);
				rows.Add(new PairRow
				{
					PairID = p.pairID, Name = p.name, Direction = p.direction, Fidelity = p.fidelityMode,
					Recurrence = p.recurrenceMode, Enabled = p.enabled,
					M365Email = l.principalEmail, GoogleEmail = r.principalEmail,
					Mappings = em.countActive(p.pairID),
					OpenDeadLetters = dl.countOpen(p.pairID)
				});
			}
			ViewBag.ShowAll = showAll;
			return View(rows);
		}

		// ── self-serve create wizard ──────────────────────────────────────────
		[HttpGet]
		public async Task<IActionResult> Create()
		{
			var model = await BuildWizard();
			return View(model);
		}

		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> Create(string sourceKey, string destKey, string flow, string mode, bool copyTitle = false)
		{
			long userId = PortalAuth.UserId(User);

			var source = ResolveCalendarKey(sourceKey, userId);
			var dest = ResolveCalendarKey(destKey, userId);
			if (source == null || dest == null)
				return await WizardFailed("Pick a source and a destination calendar.");
			if (source.Value.account.provider == dest.Value.account.provider)
				return await WizardFailed("Pick one Google calendar and one Microsoft calendar. Pairs sync across the two providers.");

			// Soft write-access check on the destination (skip silently if the
			// provider listing is unavailable; the first sync surfaces real errors).
			string? destRole = await LookupAccessRole(dest.Value.account, dest.Value.calendarId);
			if (destRole is "reader" or "freeBusyReader")
				return await WizardFailed("You only have read access to the destination calendar. Pick one you can edit, or flip the direction.");

			// Engine convention: left = m365, right = google, direction carries flow.
			var (msSide, googleSide) = source.Value.account.provider == Providers.M365 ? (source.Value, dest.Value) : (dest.Value, source.Value);
			string direction = flow == "twoway"
				? Directions.Bidirectional
				: (source.Value.account.provider == Providers.M365 ? Directions.LeftToRight : Directions.RightToLeft);

			var connL = new ProviderConnection
			{
				userID = userId, customerID = PortalAuth.CustomerId(User),
				provider = Providers.M365, principalEmail = msSide.account.principalEmail,
				calendarId = msSide.calendarId, displayName = msSide.summary,
				authKind = AuthKinds.DelegatedOauth, oauthAccountID = msSide.account.oauthAccountID
			};
			connL.ensure();
			var connR = new ProviderConnection
			{
				userID = userId, customerID = PortalAuth.CustomerId(User),
				provider = Providers.Google, principalEmail = googleSide.account.principalEmail,
				calendarId = googleSide.calendarId, displayName = googleSide.summary,
				authKind = AuthKinds.DelegatedOauth, oauthAccountID = googleSide.account.oauthAccountID
			};
			connR.ensure();
			if (connL.connectionID == 0 || connR.connectionID == 0)
				return await WizardFailed("Something went wrong saving the calendar selection. Try again.");

			string arrow = flow == "twoway" ? " ⇄ " : " → ";
			var pair = new SyncPair
			{
				userID = userId, customerID = PortalAuth.CustomerId(User),
				name = source.Value.summary + arrow + dest.Value.summary,
				leftConnectionID = connL.connectionID,
				rightConnectionID = connR.connectionID,
				direction = direction,
				fidelityMode = mode == "busy" ? Fidelity.BusyBlock : Fidelity.FullDetail,
				recurrenceMode = RecurrenceModes.Instance,
				copyTitle = copyTitle,
				windowDays = Settings.WindowDays,
				lookbackDays = Settings.LookbackDays,
				copyAttendeesToBody = Settings.CopyAttendeesToBody,
				// The first sync mirrors every in-window event; keep the circuit breaker
				// above any normal calendar's event count so it doesn't trip on day one.
				maxWritesPerRun = Math.Max(Settings.MaxWritesPerRun, 500),
				fullResyncHour = Settings.FullResyncHour,
				enabled = true
			};
			if (pair.insert() == 0)
				return await WizardFailed("Something went wrong creating the pair. Try again.");

			Common.audit($"pair-created pair={pair.pairID} user={userId} direction={direction} mode={pair.fidelityMode}");
			TempData["Info"] = $"Pair created. The first sync runs within about 5 minutes. ({pair.name})";
			return RedirectToAction("Index");
		}

		// ── pause / resume / remove ───────────────────────────────────────────
		[HttpPost, ValidateAntiForgeryToken]
		public IActionResult SetEnabled(long id, bool enabled)
		{
			var pair = new SyncPair().getById(id);
			if (!CanManage(pair)) return NotFound();

			new SyncPair().setEnabled(id, enabled);
			TempData["Info"] = $"Pair {(enabled ? "resumed" : "paused")}.";
			return RedirectToAction("Index");
		}

		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> Remove(long id)
		{
			var pair = new SyncPair().getById(id);
			if (!CanManage(pair)) return NotFound();

			var result = await CliCommands.TeardownPair(id);
			TempData[result.Removed ? "Info" : "Error"] = result.Message;
			return RedirectToAction("Index");
		}

		// ── legacy operator form (app_default connections) ────────────────────
		[HttpPost, ValidateAntiForgeryToken]
		[Authorize(Roles = "Admin")]
		public IActionResult Add(string name, string m365Email, string googleEmail, string? m365Cal,
			string? googleCal, string? direction, string? fidelity, string? recurrence)
		{
			try
			{
				long id = CliCommands.AddPair(name, m365Email, googleEmail, m365Cal ?? "primary",
					googleCal ?? "primary", direction ?? Directions.Bidirectional,
					fidelity ?? Fidelity.FullDetail, recurrence ?? RecurrenceModes.Instance);
				TempData["Info"] = $"Created sync pair {id}. Run a dry-run from the server before enabling live sync.";
			}
			catch (Exception ex)
			{
				TempData["Error"] = "Could not create pair: " + ex.Message;
			}
			return RedirectToAction("Index");
		}

		// ── helpers ───────────────────────────────────────────────────────────
		private bool CanManage(SyncPair pair) =>
			pair.pairID != 0 && (PortalAuth.IsAdmin(User) || pair.userID == PortalAuth.UserId(User));

		private async Task<PairWizardModel> BuildWizard()
		{
			long userId = PortalAuth.UserId(User);
			var model = new PairWizardModel();

			foreach (var account in new OAuthAccount().listByUser(userId))
			{
				var entry = new PairWizardModel.AccountCalendars { Account = account };
				model.Accounts.Add(entry);
				if (!account.isConnected) { entry.Error = "needs reconnect"; continue; }
				try
				{
					var probe = new ProviderConnection
					{
						provider = account.provider, principalEmail = account.principalEmail,
						calendarId = "primary", authKind = AuthKinds.DelegatedOauth,
						oauthAccountID = account.oauthAccountID
					};
					entry.Calendars = (await ProviderFactory.Create(probe).ListCalendarsAsync()).ToList();
				}
				catch (Exception ex)
				{
					Common.writeToLog($"WARN pair wizard calendar list (account {account.oauthAccountID}):", ex);
					entry.Error = "couldn't list calendars right now";
				}
			}

			bool hasGoogle = model.Accounts.Any(a => a.Account.provider == Providers.Google && a.Calendars.Count > 0);
			bool hasMs = model.Accounts.Any(a => a.Account.provider == Providers.M365 && a.Calendars.Count > 0);
			if (!hasGoogle || !hasMs)
				model.BlockReason = "Connect at least one Google and one Microsoft account first (Accounts page).";
			return model;
		}

		private async Task<IActionResult> WizardFailed(string message)
		{
			TempData["Error"] = message;
			var model = await BuildWizard();
			return View("Create", model);
		}

		// Option value format: "{oauthAccountID}:{b64url(calendarId)}:{b64url(summary)}"
		// — calendar ids on both providers can contain delimiter-unfriendly characters.
		// The summary segment is display-only (feeds the auto pair name).
		internal static string EncodeCalendarKey(long accountId, string calendarId, string summary) =>
			$"{accountId}:{B64UrlEncode(calendarId)}:{B64UrlEncode(summary)}";

		private (OAuthAccount account, string calendarId, string summary)? ResolveCalendarKey(string? key, long userId)
		{
			if (string.IsNullOrWhiteSpace(key)) return null;
			var parts = key.Split(':', 3);
			if (parts.Length < 2 || !long.TryParse(parts[0], out long accountId)) return null;

			var account = new OAuthAccount().getById(accountId);
			if (account.oauthAccountID == 0 || account.userID != userId || !account.isConnected) return null;

			string calendarId = B64UrlDecode(parts[1]);
			if (string.IsNullOrWhiteSpace(calendarId)) return null;

			string summary = parts.Length == 3 ? B64UrlDecode(parts[2]) : string.Empty;
			if (string.IsNullOrWhiteSpace(summary)) summary = account.principalEmail;
			return (account, calendarId, summary);
		}

		private static string B64UrlEncode(string s) =>
			Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? string.Empty)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

		private static string B64UrlDecode(string s)
		{
			try
			{
				string b64 = (s ?? string.Empty).Replace('-', '+').Replace('_', '/');
				return Encoding.UTF8.GetString(Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=')));
			}
			catch { return string.Empty; }
		}

		private static async Task<string?> LookupAccessRole(OAuthAccount account, string calendarId)
		{
			try
			{
				var probe = new ProviderConnection
				{
					provider = account.provider, principalEmail = account.principalEmail,
					calendarId = "primary", authKind = AuthKinds.DelegatedOauth,
					oauthAccountID = account.oauthAccountID
				};
				var cals = await ProviderFactory.Create(probe).ListCalendarsAsync();
				return cals.FirstOrDefault(c => c.Id == calendarId)?.AccessRole;
			}
			catch { return null; }
		}
	}
}
