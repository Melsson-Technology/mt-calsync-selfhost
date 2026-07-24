using System.Diagnostics;
using Core.MTCalSync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfHost.MTCalSync.Models;

namespace SelfHost.MTCalSync.Controllers
{
	// Operator dashboard: config/connection status + per-pair health, plus a
	// "Test credentials" button that runs the live setup-check on both providers.
	[Authorize(Roles = "Admin")]
	public class HomeController : Controller
	{
		[HttpGet]
		public IActionResult Index() => View(BuildModel());

		[HttpPost, ValidateAntiForgeryToken]
		public IActionResult Test()
		{
			var vm = BuildModel();
			var sc = new SetupChecker();
			vm.TestOk = sc.Run();          // live probes of Graph + Google + DB + SMTP
			vm.TestReport = sc.Lines;
			return View("Index", vm);
		}

		private static HomeViewModel BuildModel()
		{
			var vm = new HomeViewModel();

			var da = new DataAccess();
			da.execScalar("select count(*) from sync_pair", new Dictionary<string, object>());
			vm.DbOk = string.IsNullOrEmpty(da.errorMessage);
			vm.DbMessage = vm.DbOk ? "connected" : da.errorMessage;

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
