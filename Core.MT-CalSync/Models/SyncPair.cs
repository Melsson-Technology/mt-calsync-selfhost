using System.Data;

namespace Core.MTCalSync
{
	// A bidirectional (or one-way) link between one M365 (left) and one Google
	// (right) connection.
	public class SyncPair : @base
	{
		public long pairID { get; set; }
		public long userID { get; set; }
		public long customerID { get; set; }
		public string name { get; set; } = string.Empty;
		public long leftConnectionID { get; set; }     // m365
		public long rightConnectionID { get; set; }    // google
		public string direction { get; set; } = Directions.Bidirectional;
		public string fidelityMode { get; set; } = Fidelity.FullDetail;
		public string recurrenceMode { get; set; } = RecurrenceModes.Instance;
		public int windowDays { get; set; } = 60;
		public int lookbackDays { get; set; } = 1;
		public bool copyAttendeesToBody { get; set; }
		public bool copyTitle { get; set; }
		public int maxWritesPerRun { get; set; } = 25;
		public int fullResyncHour { get; set; } = 3;
		public bool enabled { get; set; } = true;
		public DateTime? nextRunAt { get; set; }
		public int runIntervalSeconds { get; set; } = 300;
		public DateTime? lastAlertAt { get; set; }

		// Resolve the connectionID for a given source provider on this pair.
		public long connectionForProvider(string provider) =>
			provider == Providers.M365 ? leftConnectionID : rightConnectionID;

		// Series preservation only applies in full-detail mode.
		public bool SeriesMode => recurrenceMode == RecurrenceModes.Series && fidelityMode == Fidelity.FullDetail;

		public SyncPair getById(long id)
		{
			var o = new SyncPair();
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@id", id } };
			try
			{
				var ds = oDA.execQuery("select * from sync_pair where pairID = @id", "DATA", "DATA", p);
				if (ds.Tables[0].Rows.Count > 0) o = dataRowToObject(ds.Tables[0].Rows[0]);
			}
			catch (Exception ex) { Common.writeToLog("ERROR SyncPair.getById:", ex); }
			return o;
		}

		public List<SyncPair> listAll(bool enabledOnly = false)
		{
			var list = new List<SyncPair>();
			var oDA = new DataAccess();
			string sql = "select * from sync_pair" + (enabledOnly ? " where enabled = 1" : "") + " order by pairID";
			try
			{
				var ds = oDA.execQuery(sql, "DATA", "DATA");
				foreach (DataRow r in ds.Tables[0].Rows) list.Add(dataRowToObject(r));
			}
			catch (Exception ex) { Common.writeToLog("ERROR SyncPair.listAll:", ex); }
			return list;
		}

		public List<SyncPair> listByUser(long userId, bool enabledOnly = false)
		{
			var list = new List<SyncPair>();
			var oDA = new DataAccess();
			string sql = "select * from sync_pair where userID = @u" + (enabledOnly ? " and enabled = 1" : "") + " order by pairID";
			var p = new Dictionary<string, object> { { "@u", userId } };
			try
			{
				var ds = oDA.execQuery(sql, "DATA", "DATA", p);
				foreach (DataRow r in ds.Tables[0].Rows) list.Add(dataRowToObject(r));
			}
			catch (Exception ex) { Common.writeToLog("ERROR SyncPair.listByUser:", ex); }
			return list;
		}

		public long insert()
		{
			var oDA = new DataAccess();
			string sql =
				"insert into sync_pair (userID, customerID, name, leftConnectionID, rightConnectionID, direction, fidelityMode, recurrenceMode, windowDays, lookbackDays, copyAttendeesToBody, copyTitle, maxWritesPerRun, fullResyncHour, enabled) " +
				"values (@user, @cust, @name, @left, @right, @direction, @fidelity, @recurrence, @windowDays, @lookbackDays, @copyAtt, @copyTitle, @maxWrites, @frh, @enabled)";
			var p = new Dictionary<string, object>
			{
				{ "@user", userID > 0 ? userID : DBNull.Value }, { "@cust", customerID > 0 ? customerID : DBNull.Value },
				{ "@name", name }, { "@left", leftConnectionID }, { "@right", rightConnectionID },
				{ "@direction", direction }, { "@fidelity", fidelityMode }, { "@recurrence", recurrenceMode },
				{ "@windowDays", windowDays },
				{ "@lookbackDays", lookbackDays }, { "@copyAtt", copyAttendeesToBody ? 1 : 0 },
				{ "@copyTitle", copyTitle ? 1 : 0 }, { "@maxWrites", maxWritesPerRun },
				{ "@frh", fullResyncHour }, { "@enabled", enabled ? 1 : 0 }
			};
			try { pairID = oDA.insertData(sql, p); errorMessage = oDA.errorMessage; }
			catch (Exception ex) { Common.writeToLog("ERROR SyncPair.insert:", ex); }
			return pairID;
		}

		public bool setEnabled(long id, bool value)
		{
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@e", value ? 1 : 0 }, { "@id", id } };
			return oDA.updateData("update sync_pair set enabled = @e where pairID = @id", p);
		}

		// Pairs the scheduler should consider this tick.
		public List<SyncPair> listDue()
		{
			var list = new List<SyncPair>();
			var oDA = new DataAccess();
			try
			{
				var ds = oDA.execQuery(
					"select * from sync_pair where enabled = 1 and (nextRunAt is null or nextRunAt <= UTC_TIMESTAMP()) order by pairID",
					"DATA", "DATA", new Dictionary<string, object>());
				foreach (DataRow r in ds.Tables[0].Rows) list.Add(dataRowToObject(r));
			}
			catch (Exception ex) { Common.writeToLog("ERROR SyncPair.listDue:", ex); }
			return list;
		}

		public void updateNextRun(long id, DateTime nextUtc)
		{
			var oDA = new DataAccess();
			oDA.updateData("update sync_pair set nextRunAt = @n where pairID = @id",
				new Dictionary<string, object> { { "@n", Common.makeMySqlDate(nextUtc) }, { "@id", id } });
		}

		// One alert email per pair per 24h (owner or operator — shared throttle).
		// Atomic claim: the UPDATE only wins when the window has passed.
		public bool shouldAlert(long id)
		{
			var oDA = new DataAccess();
			return oDA.updateData(
				"update sync_pair set lastAlertAt = UTC_TIMESTAMP() " +
				"where pairID = @id and (lastAlertAt is null or lastAlertAt < DATE_SUB(UTC_TIMESTAMP(), INTERVAL 24 HOUR))",
				new Dictionary<string, object> { { "@id", id } });
		}

		// Hard-delete a pair. FK cascade removes its event_mapping / sync_state /
		// sync_run / dead_letter / sync_lock rows. Does NOT touch provider_connection
		// (that FK is RESTRICT) or any remote calendar events — callers that want the
		// mirror events gone must delete them first (see CliCommands.TeardownPair).
		public bool delete(long id)
		{
			var oDA = new DataAccess();
			return oDA.deleteData("delete from sync_pair where pairID = @id",
				new Dictionary<string, object> { { "@id", id } }) > 0;
		}

		private SyncPair dataRowToObject(DataRow r) => new()
		{
			pairID = Common.ToLong(r["pairID"]),
			userID = Common.ToLong(r["userID"]),
			customerID = Common.ToLong(r["customerID"]),
			name = Common.ToStr(r["name"]),
			leftConnectionID = Common.ToLong(r["leftConnectionID"]),
			rightConnectionID = Common.ToLong(r["rightConnectionID"]),
			direction = Common.ToStr(r["direction"]),
			fidelityMode = Common.ToStr(r["fidelityMode"]),
			recurrenceMode = Common.ToStr(r["recurrenceMode"]),
			windowDays = Common.ToInt(r["windowDays"]),
			lookbackDays = Common.ToInt(r["lookbackDays"]),
			copyAttendeesToBody = Common.ToBool(r["copyAttendeesToBody"]),
			copyTitle = Common.ToBool(r["copyTitle"]),
			maxWritesPerRun = Common.ToInt(r["maxWritesPerRun"]),
			fullResyncHour = Common.ToInt(r["fullResyncHour"]),
			enabled = Common.ToBool(r["enabled"]),
			nextRunAt = Common.ToDateTimeUtc(r["nextRunAt"]),
			runIntervalSeconds = Math.Max(60, Common.ToInt(r["runIntervalSeconds"])),
			lastAlertAt = Common.ToDateTimeUtc(r["lastAlertAt"])
		};
	}
}
