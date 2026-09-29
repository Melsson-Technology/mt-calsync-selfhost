using System.Data;

namespace Core.MTCalSync
{
	// Incremental-sync tokens and window bookkeeping for one (pair, provider).
	public class SyncState : @base
	{
		public long syncStateID { get; set; }
		public long pairID { get; set; }
		public string provider { get; set; } = string.Empty;
		public string deltaLink { get; set; } = string.Empty;      // Graph
		public string syncToken { get; set; } = string.Empty;      // Google
		public DateTime? windowStart { get; set; }
		public DateTime? windowEnd { get; set; }
		public DateTime? lastFullResyncAt { get; set; }
		public DateTime? lastSuccessfulRunAt { get; set; }
		public int consecutiveFailures { get; set; }
		public string lastError { get; set; } = string.Empty;

		public bool HasToken => !string.IsNullOrEmpty(deltaLink) || !string.IsNullOrEmpty(syncToken);

		// Returns an empty state (syncStateID 0) when there is no row yet.
		public SyncState getByPairProvider(long pair, string prov)
		{
			var o = new SyncState { pairID = pair, provider = prov };
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@p", pair }, { "@prov", prov } };
			try
			{
				var ds = oDA.execQuery("select * from sync_state where pairID=@p and provider=@prov limit 1", "DATA", "DATA", p);
				if (ds.Tables[0].Rows.Count > 0) o = dataRowToObject(ds.Tables[0].Rows[0]);
			}
			catch (Exception ex) { Common.writeToLog("ERROR SyncState.getByPairProvider:", ex); }
			return o;
		}

		// After all pages are read, saves the new token and the window it covers, and records
		// the success (and the full resync, if it was one).
		public void saveAfterRun(string? newToken, RollingWindow window, bool wasFull)
		{
			var oDA = new DataAccess();
			bool isGraph = provider == Providers.M365;
			string dl = isGraph ? (newToken ?? string.Empty) : string.Empty;
			string st = isGraph ? string.Empty : (newToken ?? string.Empty);
			string sql =
				"insert into sync_state (pairID, provider, deltaLink, syncToken, windowStart, windowEnd, lastFullResyncAt, lastSuccessfulRunAt, consecutiveFailures, lastError) " +
				"values (@p, @prov, @dl, @st, @ws, @we, @full, NOW(), 0, '') " +
				"on duplicate key update deltaLink=@dl2, syncToken=@st2, windowStart=@ws2, windowEnd=@we2, " +
				(wasFull ? "lastFullResyncAt=NOW(), " : "") +
				"lastSuccessfulRunAt=NOW(), consecutiveFailures=0, lastError=''";
			var p = new Dictionary<string, object>
			{
				{ "@p", pairID }, { "@prov", provider }, { "@dl", dl }, { "@st", st },
				{ "@ws", Common.makeMySqlDate(window.StartUtc) }, { "@we", Common.makeMySqlDate(window.EndUtc) },
				{ "@full", wasFull ? Common.getMySqlNow() : (object)DBNull.Value },
				{ "@dl2", dl }, { "@st2", st },
				{ "@ws2", Common.makeMySqlDate(window.StartUtc) }, { "@we2", Common.makeMySqlDate(window.EndUtc) }
			};
			try { oDA.insertData(sql, p); }
			catch (Exception ex) { Common.writeToLog("ERROR SyncState.saveAfterRun:", ex); }
		}

		public void recordFailure(string error)
		{
			var oDA = new DataAccess();
			string sql =
				"insert into sync_state (pairID, provider, consecutiveFailures, lastError) values (@p, @prov, 1, @err) " +
				"on duplicate key update consecutiveFailures = consecutiveFailures + 1, lastError = @err2";
			var p = new Dictionary<string, object>
			{
				{ "@p", pairID }, { "@prov", provider },
				{ "@err", error ?? string.Empty }, { "@err2", error ?? string.Empty }
			};
			try { oDA.insertData(sql, p); }
			catch (Exception ex) { Common.writeToLog("ERROR SyncState.recordFailure:", ex); }
		}

		// Clears both sides' tokens, so the next run does a full sync.
		public void resetTokens(long pair)
		{
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@p", pair } };
			oDA.updateData("update sync_state set deltaLink='', syncToken='' where pairID=@p", p);
		}

		private SyncState dataRowToObject(DataRow r) => new()
		{
			syncStateID = Common.ToLong(r["syncStateID"]),
			pairID = Common.ToLong(r["pairID"]),
			provider = Common.ToStr(r["provider"]),
			deltaLink = Common.ToStr(r["deltaLink"]),
			syncToken = Common.ToStr(r["syncToken"]),
			windowStart = Common.ToDateTimeUtc(r["windowStart"]),
			windowEnd = Common.ToDateTimeUtc(r["windowEnd"]),
			lastFullResyncAt = Common.ToDateTimeUtc(r["lastFullResyncAt"]),
			lastSuccessfulRunAt = Common.ToDateTimeUtc(r["lastSuccessfulRunAt"]),
			consecutiveFailures = Common.ToInt(r["consecutiveFailures"]),
			lastError = Common.ToStr(r["lastError"])
		};
	}
}
