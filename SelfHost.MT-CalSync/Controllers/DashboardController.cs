using Core.MTCalSync;
using Microsoft.AspNetCore.Mvc;
using SelfHost.MTCalSync.Models;

namespace SelfHost.MTCalSync.Controllers
{
	// The operator's home: the setup guide until a pair exists, connected accounts (with
	// reconnect nudges), and every sync pair with its latest run health.
	public class DashboardController : Controller
	{
		public IActionResult Index()
		{
			long userId = PortalAuth.UserId(User);

			var accounts = new OAuthAccount().listByUser(userId);

			var rows = new List<PairRow>();
			var em = new EventMapping();
			var dl = new DeadLetter();
			// All pairs, not the user's: a self-host install has exactly one operator, and
			// pairs from the operator form, `add-pair` and Shared calendars carry no user,
			// so a per-user list hid them and showed the setup guide over running pairs.
			foreach (var p in new SyncPair().listAll())
			{
				var l = new ProviderConnection().getById(p.leftConnectionID);
				var r = new ProviderConnection().getById(p.rightConnectionID);
				var lastRuns = new SyncRun().recent(p.pairID, 1);
				var last = lastRuns.Count > 0 ? lastRuns[0] : null;
				rows.Add(new PairRow
				{
					PairID = p.pairID, Name = p.name, Direction = p.direction, Fidelity = p.fidelityMode,
					Recurrence = p.recurrenceMode, Enabled = p.enabled,
					M365Email = l.principalEmail, GoogleEmail = r.principalEmail,
					M365Token = last != null, GoogleToken = last != null,
					LastSuccess = last == null ? "never"
						: $"{last.status} · {(last.finishedAt ?? last.startedAt):MM-dd HH:mm}Z",
					Mappings = em.countActive(p.pairID),
					OpenDeadLetters = dl.countOpen(p.pairID)
				});
			}

			var vm = new DashboardViewModel
			{
				IsAdmin = PortalAuth.IsAdmin(User),
				Accounts = accounts,
				Pairs = rows,
				GoogleConnected = accounts.Any(a => a.provider == Providers.Google && a.isConnected),
				MicrosoftConnected = accounts.Any(a => a.provider == Providers.M365 && a.isConnected),
				HasPairs = rows.Count > 0,
			};
			return View(vm);
		}
	}
}
