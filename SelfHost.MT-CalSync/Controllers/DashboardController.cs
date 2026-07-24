using Core.MTCalSync;
using Microsoft.AspNetCore.Mvc;
using SelfHost.MTCalSync.Models;

namespace SelfHost.MTCalSync.Controllers
{
	// The signed-in user's home: subscription/trial state (banner or blocking card
	// per the entitlement), connected accounts (with reconnect nudges), and sync
	// pairs with their latest run health. Admins get a link to the operator pages.
	public class DashboardController : Controller
	{
		public IActionResult Index()
		{
			long userId = PortalAuth.UserId(User);

			var accounts = new OAuthAccount().listByUser(userId);

			var rows = new List<PairRow>();
			var em = new EventMapping();
			var dl = new DeadLetter();
			foreach (var p in new SyncPair().listByUser(userId))
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
