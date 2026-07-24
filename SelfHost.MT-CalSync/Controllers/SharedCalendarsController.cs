using Core.MTCalSync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfHost.MTCalSync.Models;

namespace SelfHost.MTCalSync.Controllers
{
	// /SharedCalendars — discover the shared Google calendars visible to the configured
	// Google account and toggle each one's one-way mirror into the M365 primary calendar.
	// Checking a box creates a google→m365 pair; unchecking removes it (and its mirrored
	// events). Operator-only: it drives the global-credential (app_default) connections.
	[Authorize(Roles = "Admin")]
	public class SharedCalendarsController : Controller
	{
		[HttpGet]
		public async Task<IActionResult> Index()
		{
			var vm = new SharedCalendarsViewModel();
			var (m365, gConn, error) = Resolve();
			vm.M365Email = m365;
			vm.GoogleEmail = gConn?.principalEmail ?? string.Empty;
			if (error != null) { vm.Error = error; return View(vm); }

			try
			{
				var provider = ProviderFactory.Create(gConn!);
				var cals = await provider.ListCalendarsAsync();
				var em = new EventMapping();
				foreach (var c in cals.Where(c => !c.Primary).OrderBy(c => c.Summary, StringComparer.OrdinalIgnoreCase))
				{
					var pair = CliCommands.FindSharedPair(gConn!.principalEmail, c.Id);
					vm.Calendars.Add(new SharedCalendarRow
					{
						CalendarId = c.Id,
						Summary = string.IsNullOrWhiteSpace(c.Summary) ? c.Id : c.Summary,
						AccessRole = c.AccessRole,
						IsSynced = pair != null,
						Enabled = pair?.enabled ?? false,
						PairId = pair?.pairID ?? 0,
						Mappings = pair != null ? em.countActive(pair.pairID) : 0
					});
				}
				vm.Ready = true;
			}
			catch (Exception ex)
			{
				vm.Error = "Could not list Google calendars: " + ex.Message;
			}
			return View(vm);
		}

		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> Toggle(string calendarId, string summary, bool enable)
		{
			var (m365, gConn, error) = Resolve();
			if (error != null) { TempData["Error"] = error; return RedirectToAction("Index"); }
			if (string.IsNullOrWhiteSpace(calendarId)) { TempData["Error"] = "Missing calendar id."; return RedirectToAction("Index"); }

			try
			{
				if (enable)
				{
					long id = CliCommands.EnableSharedCalendarMirror(m365, gConn!.principalEmail, calendarId, summary);
					TempData["Info"] = $"Now mirroring \"{summary}\" into your M365 primary calendar (pair {id}). Events appear on the next sync (within ~5 min).";
				}
				else
				{
					var pair = CliCommands.FindSharedPair(gConn!.principalEmail, calendarId);
					if (pair == null) { TempData["Error"] = "That calendar isn't currently synced."; return RedirectToAction("Index"); }
					var res = await CliCommands.TeardownPair(pair.pairID);
					TempData[res.Removed ? "Info" : "Error"] = res.Message;
				}
			}
			catch (Exception ex) { TempData["Error"] = "Action failed: " + ex.Message; }
			return RedirectToAction("Index");
		}

		// Resolve the destination M365 mailbox + the Google account to enumerate. Prefer the
		// main bidirectional pair's connections; fall back to any configured connections.
		private static (string m365Email, ProviderConnection? googleConn, string? error) Resolve()
		{
			var pairs = new SyncPair().listAll();
			ProviderConnection? gConn = null;
			string m365 = string.Empty;

			var main = pairs.FirstOrDefault(p => p.direction == Directions.Bidirectional);
			if (main != null)
			{
				gConn = new ProviderConnection().getById(main.rightConnectionID);
				m365 = new ProviderConnection().getById(main.leftConnectionID).principalEmail;
			}
			if (gConn == null || string.IsNullOrEmpty(gConn.principalEmail) || string.IsNullOrEmpty(m365))
			{
				var conns = new ProviderConnection().listAll();
				gConn ??= conns.FirstOrDefault(c => c.provider == Providers.Google);
				if (string.IsNullOrEmpty(m365))
					m365 = conns.FirstOrDefault(c => c.provider == Providers.M365)?.principalEmail ?? string.Empty;
			}
			if (gConn == null || string.IsNullOrEmpty(gConn.principalEmail) || string.IsNullOrEmpty(m365))
				return (m365, gConn, "No Microsoft 365 mailbox and Google account are configured yet. Create your main calendar pair on the Pairs page first, then return here.");
			return (m365, gConn, null);
		}
	}
}
