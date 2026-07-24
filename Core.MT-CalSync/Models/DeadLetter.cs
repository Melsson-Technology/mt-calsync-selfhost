using System.Data;

namespace Core.MTCalSync
{
	// Items that fail permanently or repeatedly. The UNIQUE(pairID, sourceProvider,
	// sourceEventKey, operation) makes a re-failing item bump attemptCount; open rows
	// are skipped by the engine so one poison event can't fail the whole pair.
	public class DeadLetter : @base
	{
		public long deadLetterID { get; set; }
		public long pairID { get; set; }
		public long mappingID { get; set; }
		public string sourceProvider { get; set; } = string.Empty;
		public string sourceEventId { get; set; } = string.Empty;
		public string operation { get; set; } = string.Empty;   // create|update|delete
		public string payloadJson { get; set; } = string.Empty;
		public string errorCode { get; set; } = string.Empty;
		public string errorText { get; set; } = string.Empty;
		public int attemptCount { get; set; }

		public void record(long pair, string provider, string eventId, string op, string payload, string code, string error)
		{
			var oDA = new DataAccess();
			string key = string.IsNullOrEmpty(eventId) ? string.Empty : Common.Sha256Hex(eventId);
			string sql =
				"insert into dead_letter (pairID, sourceProvider, sourceEventId, sourceEventKey, operation, payloadJson, errorCode, errorText) " +
				"values (@p, @prov, @eid, @ek, @op, @payload, @code, @err) " +
				"on duplicate key update attemptCount = attemptCount + 1, lastFailedAt = NOW(), errorCode=@code2, errorText=@err2, resolved=0, resolvedAt=NULL";
			var p = new Dictionary<string, object>
			{
				{ "@p", pair }, { "@prov", provider }, { "@eid", eventId ?? string.Empty }, { "@ek", key },
				{ "@op", op }, { "@payload", payload ?? string.Empty }, { "@code", code ?? string.Empty },
				{ "@err", error ?? string.Empty }, { "@code2", code ?? string.Empty }, { "@err2", error ?? string.Empty }
			};
			try { oDA.insertData(sql, p); } catch (Exception ex) { Common.writeToLog("ERROR DeadLetter.record:", ex); }
		}

		public bool hasOpen(long pair, string provider, string eventId)
		{
			if (string.IsNullOrEmpty(eventId)) return false;
			var oDA = new DataAccess();
			string key = Common.Sha256Hex(eventId);
			var p = new Dictionary<string, object> { { "@p", pair }, { "@prov", provider }, { "@k", key } };
			object? v = oDA.execScalar(
				"select count(*) from dead_letter where pairID=@p and sourceProvider=@prov and sourceEventKey=@k and resolved=0", p);
			return Common.ToInt(v) > 0;
		}

		public int countOpen(long pair)
		{
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@p", pair } };
			return Common.ToInt(oDA.execScalar("select count(*) from dead_letter where pairID=@p and resolved=0", p));
		}

		public List<DeadLetter> listOpen(long pair)
		{
			var list = new List<DeadLetter>();
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@p", pair } };
			try
			{
				var ds = oDA.execQuery("select * from dead_letter where pairID=@p and resolved=0 order by deadLetterID", "DATA", "DATA", p);
				foreach (DataRow r in ds.Tables[0].Rows)
				{
					list.Add(new DeadLetter
					{
						deadLetterID = Common.ToLong(r["deadLetterID"]), pairID = Common.ToLong(r["pairID"]),
						sourceProvider = Common.ToStr(r["sourceProvider"]), sourceEventId = Common.ToStr(r["sourceEventId"]),
						operation = Common.ToStr(r["operation"]), errorCode = Common.ToStr(r["errorCode"]),
						errorText = Common.ToStr(r["errorText"]), attemptCount = Common.ToInt(r["attemptCount"])
					});
				}
			}
			catch (Exception ex) { Common.writeToLog("ERROR DeadLetter.listOpen:", ex); }
			return list;
		}

		// Resolve one (id>0) or all open rows for a pair. Returns rows affected.
		public long resolve(long pair, long id = 0)
		{
			var oDA = new DataAccess();
			if (id > 0)
			{
				var p = new Dictionary<string, object> { { "@id", id } };
				return oDA.updateData("update dead_letter set resolved=1, resolvedAt=NOW() where deadLetterID=@id", p) ? 1 : 0;
			}
			var pa = new Dictionary<string, object> { { "@p", pair } };
			return oDA.updateData("update dead_letter set resolved=1, resolvedAt=NOW() where pairID=@p and resolved=0", pa) ? 1 : 0;
		}
	}
}
