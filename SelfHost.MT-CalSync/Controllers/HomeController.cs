using System.Diagnostics;
using Core.MTCalSync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfHost.MTCalSync.Models;

namespace SelfHost.MTCalSync.Controllers
{
	// The System page: configuration status and per-pair health, plus "Test credentials",
	// which runs setup-check (the database, the settings, and a live read of every
	// calendar that belongs to a pair).
	[Authorize(Roles = "Admin")]
	public class HomeController : Controller
	{
		[HttpGet]
		public IActionResult Index()
		{
			var vm = BuildModel();
			if (TempData["TestReport"] is string report)
			{
				vm.TestReport = report.Split('\n').ToList();
				vm.TestOk = TempData["TestOk"] as bool?;
				vm.TestCalendars = TempData["TestCalendars"] as int? ?? 0;
			}
			return View(vm);
		}

		// Post/redirect/get. The result used to render straight from the POST, so
		// refreshing the page re-submitted the form and ran every probe again.
		[HttpPost, ValidateAntiForgeryToken]
		public IActionResult Test()
		{
			var sc = new SetupChecker();
			TempData["TestOk"] = sc.Run();
			TempData["TestReport"] = string.Join("\n", sc.Lines);
			TempData["TestCalendars"] = sc.CalendarsChecked;
			return RedirectToAction("Index");
		}

		private static HomeViewModel BuildModel()
		{
			var vm = new HomeViewModel();

			var da = new DataAccess();
			da.execScalar("select count(*) from sync_pair", new Dictionary<string, object>());
			vm.DbOk = string.IsNullOrEmpty(da.errorMessage);
			vm.DbMessage = vm.DbOk ? "connected" : da.errorMessage;

			// Sign-in clients (what the Calendars page needs) are reported apart from the
			// app-only credentials. Showing only the latter, as "Microsoft 365" and
			// "Google", read "incomplete" on a correctly set-up delegated install.
			vm.MsOAuthConfigured = !string.IsNullOrWhiteSpace(Settings.MsOAuthClientId)
				&& !string.IsNullOrWhiteSpace(Settings.EffectiveMsOAuthClientSecret);
			vm.GoogleOAuthConfigured = !string.IsNullOrWhiteSpace(Settings.GoogleOAuthClientId)
				&& !string.IsNullOrWhiteSpace(Settings.EffectiveGoogleOAuthClientSecret);
			vm.GraphConfigured = !string.IsNullOrWhiteSpace(Settings.GraphTenantId)
				&& !string.IsNullOrWhiteSpace(Settings.GraphClientId)
				&& !string.IsNullOrWhiteSpace(Settings.EffectiveGraphClientSecret);
			vm.GoogleConfigured = !string.IsNullOrWhiteSpace(Settings.EffectiveGoogleServiceAccountJson);
			vm.SmtpConfigured = !string.IsNullOrWhiteSpace(Settings.SmtpHost) && !string.IsNullOrWhiteSpace(Settings.AlertTo);

			if (vm.DbOk)
			{
				var em = new EventMapping();
				var dl = new DeadLetter();
				foreach (var p in new SyncPair().listAll())
				{
					var l = new ProviderConnection().getById(p.leftConnectionID);
					var r = new ProviderConnection().getById(p.rightConnectionID);
					var sL = new SyncState().getByPairProvider(p.pairID, Providers.M365);
					var sR = new SyncState().getByPairProvider(p.pairID, Providers.Google);
					vm.Pairs.Add(new PairRow
					{
						PairID = p.pairID, Name = p.name, Direction = p.direction, Fidelity = p.fidelityMode,
						Recurrence = p.recurrenceMode, Enabled = p.enabled,
						M365Email = l.principalEmail, GoogleEmail = r.principalEmail,
						M365Token = sL.HasToken, GoogleToken = sR.HasToken,
						LastSuccess = sL.lastSuccessfulRunAt?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "never",
						Mappings = em.countActive(p.pairID), OpenDeadLetters = dl.countOpen(p.pairID)
					});
				}
			}
			return vm;
		}

		[AllowAnonymous]
		[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
		public IActionResult Error() =>
			View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
	}
}
