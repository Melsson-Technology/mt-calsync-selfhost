using System.Data;
using Core.MTCalSync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfHost.MTCalSync.Models;

namespace SelfHost.MTCalSync.Controllers
{
	// /Admin — operator monitoring: fleet-wide pair health, accounts needing
	// reconnect, open dead letters, and signup/trial counts at a glance.
	[Authorize(Roles = "Admin")]
	public class AdminController : Controller
	{
		[HttpGet]
		public IActionResult Monitoring()
		{
			var model = new MonitoringModel();
			var oDA = new DataAccess();

			try
			{
				var ds = oDA.execQuery(
					"select sp.pairID, sp.name, sp.enabled, sp.nextRunAt, sp.direction, " +
					"  (select status from sync_run r where r.pairID = sp.pairID order by r.runID desc limit 1) as lastStatus, " +
					"  (select finishedAt from sync_run r where r.pairID = sp.pairID order by r.runID desc limit 1) as lastFinishedAt, " +
					"  (select count(*) from dead_letter d where d.pairID = sp.pairID and d.resolved = 0) as openDeadLetters " +
					"from sync_pair sp order by sp.pairID",
					"DATA", "DATA", new Dictionary<string, object>());
				foreach (DataRow r in ds.Tables[0].Rows)
				{
					model.Pairs.Add(new MonitoringModel.PairHealth
					{
						PairID = Common.ToLong(r["pairID"]),
						Name = Common.ToStr(r["name"]),
						Direction = Common.ToStr(r["direction"]),
						Enabled = Common.ToBool(r["enabled"]),
						NextRunAt = Common.ToDateTimeUtc(r["nextRunAt"]),
						LastStatus = Common.ToStr(r["lastStatus"]),
						LastFinishedAt = Common.ToDateTimeUtc(r["lastFinishedAt"]),
						OpenDeadLetters = Common.ToInt(r["openDeadLetters"])
					});
				}

				model.DuePairs = Common.ToInt(oDA.execScalar(
					"select count(*) from sync_pair where enabled = 1 and (nextRunAt is null or nextRunAt <= UTC_TIMESTAMP())",
					new Dictionary<string, object>()));
				model.ReauthAccounts = Common.ToInt(oDA.execScalar(
					"select count(*) from oauth_account where status <> 'connected'", new Dictionary<string, object>()));
				model.OpenDeadLetters = Common.ToInt(oDA.execScalar(
					"select count(*) from dead_letter where resolved = 0", new Dictionary<string, object>()));
			}
			catch (Exception ex)
			{
				Common.writeToLog("ERROR Admin.Monitoring:", ex);
				model.Error = ex.Message;
			}
			return View(model);
		}
	}
}
