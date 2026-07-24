namespace Core.MTCalSync
{
	// Per-run audit row + live counters the engine increments during a run.
	public class SyncRun : @base
	{
		public long runID { get; set; }
		public long pairID { get; set; }
		public string triggerType { get; set; } = "timer";     // timer|manual
		public string syncType { get; set; } = "incremental";  // incremental|full_resync
		public string status { get; set; } = "running";
		public DateTime? startedAt { get; set; }
		public DateTime? finishedAt { get; set; }

		public int leftChanges, rightChanges;
		public int createdCount, updatedCount, deletedCount, skippedCount;
		public int echoSkippedCount, conflictCount, adoptedCount, deadLetteredCount;
		public string errorText { get; set; } = string.Empty;

		public static SyncRun start(long pairID, string triggerType, string syncType)
		{
			var run = new SyncRun { pairID = pairID, triggerType = triggerType, syncType = syncType, status = "running" };
			var oDA = new DataAccess();
			string sql = "insert into sync_run (pairID, triggerType, syncType, status) values (@p, @t, @s, 'running')";
			var p = new Dictionary<string, object> { { "@p", pairID }, { "@t", triggerType }, { "@s", syncType } };
			try { run.runID = oDA.insertData(sql, p); } catch (Exception ex) { Common.writeToLog("ERROR SyncRun.start:", ex); }
			return run;
		}

		public void finish(string finalStatus)
		{
			status = finalStatus;
			if (runID <= 0) return;
			var oDA = new DataAccess();
			string sql =
				"update sync_run set finishedAt=NOW(), status=@status, leftChanges=@lc, rightChanges=@rc, createdCount=@cc, " +
				"updatedCount=@uc, deletedCount=@dc, skippedCount=@sc, echoSkippedCount=@ec, conflictCount=@cf, adoptedCount=@ad, " +
				"deadLetteredCount=@dl, errorText=@err where runID=@id";
			var p = new Dictionary<string, object>
			{
				{ "@status", finalStatus }, { "@lc", leftChanges }, { "@rc", rightChanges }, { "@cc", createdCount },
				{ "@uc", updatedCount }, { "@dc", deletedCount }, { "@sc", skippedCount }, { "@ec", echoSkippedCount },
				{ "@cf", conflictCount }, { "@ad", adoptedCount }, { "@dl", deadLetteredCount },
				{ "@err", errorText ?? string.Empty }, { "@id", runID }
			};
			try { oDA.updateData(sql, p); } catch (Exception ex) { Common.writeToLog("ERROR SyncRun.finish:", ex); }
		}

		public string Summary() =>
			$"pair={pairID} run={runID} status={status} created={createdCount} updated={updatedCount} deleted={deletedCount} " +
			$"skipped={skippedCount} echo={echoSkippedCount} conflicts={conflictCount} adopted={adoptedCount} deadletter={deadLetteredCount}";

		// Recent run history for the CLI `history` command.
		public List<SyncRun> recent(long pairID, int limit = 20)
		{
			var list = new List<SyncRun>();
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@p", pairID }, { "@lim", limit } };
			try
			{
				var ds = oDA.execQuery("select * from sync_run where pairID=@p order by runID desc limit @lim", "DATA", "DATA", p);
				foreach (System.Data.DataRow r in ds.Tables[0].Rows)
				{
					list.Add(new SyncRun
					{
						runID = Common.ToLong(r["runID"]), pairID = Common.ToLong(r["pairID"]),
						triggerType = Common.ToStr(r["triggerType"]), syncType = Common.ToStr(r["syncType"]),
						status = Common.ToStr(r["status"]),
						startedAt = Common.ToDateTimeUtc(r["startedAt"]), finishedAt = Common.ToDateTimeUtc(r["finishedAt"]),
						createdCount = Common.ToInt(r["createdCount"]), updatedCount = Common.ToInt(r["updatedCount"]),
						deletedCount = Common.ToInt(r["deletedCount"]), skippedCount = Common.ToInt(r["skippedCount"]),
						echoSkippedCount = Common.ToInt(r["echoSkippedCount"]), conflictCount = Common.ToInt(r["conflictCount"]),
						adoptedCount = Common.ToInt(r["adoptedCount"]), deadLetteredCount = Common.ToInt(r["deadLetteredCount"]),
						errorText = Common.ToStr(r["errorText"])
					});
				}
			}
			catch (Exception ex) { Common.writeToLog("ERROR SyncRun.recent:", ex); }
			return list;
		}
	}
}
