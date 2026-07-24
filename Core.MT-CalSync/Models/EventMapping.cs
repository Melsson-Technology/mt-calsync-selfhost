using System.Data;

namespace Core.MTCalSync
{
	// The crux cross-reference. One row links a left (M365) event to a right
	// (Google) event; originProvider says which side is authoritative for this unit.
	// Long provider ids are indexed via their SHA-256 (*EventKey) — see schema.
	public class EventMapping : @base
	{
		public long mappingID { get; set; }
		public long pairID { get; set; }
		public string originProvider { get; set; } = string.Empty;

		public string leftEventId { get; set; } = string.Empty;
		public string leftEventKey { get; set; } = string.Empty;
		public string leftICalUid { get; set; } = string.Empty;
		public string leftEtag { get; set; } = string.Empty;
		public string leftSeriesMasterId { get; set; } = string.Empty;

		public string rightEventId { get; set; } = string.Empty;
		public string rightEventKey { get; set; } = string.Empty;
		public string rightICalUid { get; set; } = string.Empty;
		public string rightEtag { get; set; } = string.Empty;
		public string rightRecurringEventId { get; set; } = string.Empty;

		public string unitKind { get; set; } = UnitKinds.Single;
		public string originSeriesKey { get; set; } = string.Empty;
		public DateTime? occurrenceOriginalStart { get; set; }

		public string projectedHash { get; set; } = string.Empty;
		public string originContentHash { get; set; } = string.Empty;

		public string status { get; set; } = "active";
		public long mirrorVersion { get; set; }
		public DateTime? lastSyncedAt { get; set; }
		public string lastSyncDirection { get; set; } = string.Empty;
		public DateTime? tombstonedAt { get; set; }

		// ── side accessors ───────────────────────────────────────────────────
		public string EventIdForSide(string side) => side == Providers.M365 ? leftEventId : rightEventId;
		public string EtagForSide(string side) => side == Providers.M365 ? leftEtag : rightEtag;
		public string MirrorSide => Providers.Other(originProvider);
		public string MirrorEventId => EventIdForSide(MirrorSide);

		// Any LIVE mapping (any pair) already tracking this provider event on the
		// given side? The stray sweep uses this so adopted mirrors — which keep
		// their old pair's stamp until the next rewrite — are never deleted.
		public bool existsActiveForSide(string side, string eventId)
		{
			if (string.IsNullOrEmpty(eventId)) return false;
			string col = side == Providers.M365 ? "leftEventKey" : "rightEventKey";
			object? v = new DataAccess().execScalar(
				$"select count(*) from event_mapping where {col} = @k and status = 'active'",
				new Dictionary<string, object> { { "@k", Common.Sha256Hex(eventId) } });
			return Convert.ToInt64(v ?? 0) > 0;
		}

		public void SetSide(string side, string id, string ical, string etag)
		{
			if (side == Providers.M365)
			{
				leftEventId = id ?? string.Empty;
				leftEventKey = string.IsNullOrEmpty(id) ? string.Empty : Common.Sha256Hex(id);
				if (!string.IsNullOrEmpty(ical)) leftICalUid = ical;
				leftEtag = etag ?? string.Empty;
			}
			else
			{
				rightEventId = id ?? string.Empty;
				rightEventKey = string.IsNullOrEmpty(id) ? string.Empty : Common.Sha256Hex(id);
				if (!string.IsNullOrEmpty(ical)) rightICalUid = ical;
				rightEtag = etag ?? string.Empty;
			}
		}

		// ── finders ──────────────────────────────────────────────────────────
		// Any active/tombstoned mapping whose <side> event id hashes to eventKey.
		public EventMapping? findBySideKey(long pair, string side, string eventKey)
		{
			if (string.IsNullOrEmpty(eventKey)) return null;
			string col = side == Providers.M365 ? "leftEventKey" : "rightEventKey";
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@p", pair }, { "@k", eventKey } };
			try
			{
				var ds = oDA.execQuery($"select * from event_mapping where pairID=@p and {col}=@k limit 1", "DATA", "DATA", p);
				if (ds.Tables[0].Rows.Count > 0) return dataRowToObject(ds.Tables[0].Rows[0]);
			}
			catch (Exception ex) { Common.writeToLog("ERROR EventMapping.findBySideKey:", ex); }
			return null;
		}

		public EventMapping? findByOriginId(long pair, string originProv, string originId) =>
			findBySideKey(pair, originProv, string.IsNullOrEmpty(originId) ? string.Empty : Common.Sha256Hex(originId));

		// Active mapping for this pair where `originSide` is the authoritative side and the
		// stored origin iCalUId matches. The origin iCalUId is STABLE across provider id-churn
		// (Exchange reissues an event's id on some edits/accepts), so this recovers a mapping
		// the side-key lookup lost — the alternative being a spurious Create (duplicate mirror).
		// Index-served by idx_map_left_ical / idx_map_right_ical (pairID, {left|right}ICalUid).
		public EventMapping? findActiveByOriginUid(long pair, string originSide, string iCalUid)
		{
			if (string.IsNullOrEmpty(iCalUid)) return null;
			string col = originSide == Providers.M365 ? "leftICalUid" : "rightICalUid";
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@p", pair }, { "@s", originSide }, { "@u", iCalUid } };
			try
			{
				var ds = oDA.execQuery(
					$"select * from event_mapping where pairID=@p and originProvider=@s and {col}=@u and status='active' order by mappingID limit 1",
					"DATA", "DATA", p);
				if (ds.Tables[0].Rows.Count > 0) return dataRowToObject(ds.Tables[0].Rows[0]);
			}
			catch (Exception ex) { Common.writeToLog("ERROR EventMapping.findActiveByOriginUid:", ex); }
			return null;
		}

		// True when a DIFFERENT pair holds an ACTIVE mapping in which `eventKey` is that
		// pair's MIRROR on `side` (its originProvider is the OTHER side). This is how we
		// recognise an event that another pair wrote into a SHARED destination calendar,
		// so a pair polling that calendar never re-mirrors a sibling pair's mirror.
		// Requires idx_map_left_key / idx_map_right_key (migration 003) to be index-served.
		public bool isMirrorInAnotherPair(long thisPairId, string side, string eventKey)
		{
			if (string.IsNullOrEmpty(eventKey)) return false;
			string col = side == Providers.M365 ? "leftEventKey" : "rightEventKey";
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@p", thisPairId }, { "@k", eventKey }, { "@s", side } };
			try
			{
				object? v = oDA.execScalar(
					$"select 1 from event_mapping where pairID<>@p and {col}=@k and status='active' and originProvider<>@s limit 1", p);
				return v != null;
			}
			catch (Exception ex) { Common.writeToLog("ERROR EventMapping.isMirrorInAnotherPair:", ex); return false; }
		}

		// Active occurrence mapping keyed by its stable (series, originalStart).
		public EventMapping? findByOccurrence(long pair, string originProv, string seriesKey, DateTime? occStartUtc)
		{
			if (string.IsNullOrEmpty(seriesKey) || occStartUtc == null) return null;
			var oDA = new DataAccess();
			var p = new Dictionary<string, object>
			{
				{ "@p", pair }, { "@op", originProv }, { "@sk", seriesKey },
				{ "@os", Common.makeMySqlDate(occStartUtc.Value) }
			};
			try
			{
				var ds = oDA.execQuery(
					"select * from event_mapping where pairID=@p and originProvider=@op and originSeriesKey=@sk and occurrenceOriginalStart=@os and status='active' limit 1",
					"DATA", "DATA", p);
				if (ds.Tables[0].Rows.Count > 0) return dataRowToObject(ds.Tables[0].Rows[0]);
			}
			catch (Exception ex) { Common.writeToLog("ERROR EventMapping.findByOccurrence:", ex); }
			return null;
		}

		// Active occurrence/single mappings for a series (series-shrink diffing).
		public List<EventMapping> listActiveForSeries(long pair, string originProv, string seriesKey)
		{
			var list = new List<EventMapping>();
			if (string.IsNullOrEmpty(seriesKey)) return list;
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@p", pair }, { "@op", originProv }, { "@sk", seriesKey } };
			try
			{
				var ds = oDA.execQuery(
					"select * from event_mapping where pairID=@p and originProvider=@op and originSeriesKey=@sk and status='active'",
					"DATA", "DATA", p);
				foreach (DataRow r in ds.Tables[0].Rows) list.Add(dataRowToObject(r));
			}
			catch (Exception ex) { Common.writeToLog("ERROR EventMapping.listActiveForSeries:", ex); }
			return list;
		}

		public List<EventMapping> listByPair(long pair, bool includeTombstoned = false)
		{
			var list = new List<EventMapping>();
			var oDA = new DataAccess();
			string sql = "select * from event_mapping where pairID=@p" + (includeTombstoned ? "" : " and status='active'") + " order by mappingID";
			var p = new Dictionary<string, object> { { "@p", pair } };
			try
			{
				var ds = oDA.execQuery(sql, "DATA", "DATA", p);
				foreach (DataRow r in ds.Tables[0].Rows) list.Add(dataRowToObject(r));
			}
			catch (Exception ex) { Common.writeToLog("ERROR EventMapping.listByPair:", ex); }
			return list;
		}

		public int countActive(long pair)
		{
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@p", pair } };
			object? v = oDA.execScalar("select count(*) from event_mapping where pairID=@p and status='active'", p);
			return Common.ToInt(v);
		}

		// ── writes ─────────────────────────────────────────────────────────
		// Insert (mappingID==0) or update by id. EventKeys are (re)derived here.
		public long save()
		{
			leftEventKey = string.IsNullOrEmpty(leftEventId) ? string.Empty : Common.Sha256Hex(leftEventId);
			rightEventKey = string.IsNullOrEmpty(rightEventId) ? string.Empty : Common.Sha256Hex(rightEventId);
			var oDA = new DataAccess();
			var p = new Dictionary<string, object>
			{
				{ "@pairID", pairID }, { "@originProvider", originProvider },
				{ "@leftEventId", NullIfEmpty(leftEventId) }, { "@leftEventKey", NullIfEmpty(leftEventKey) },
				{ "@leftICalUid", NullIfEmpty(leftICalUid) }, { "@leftEtag", NullIfEmpty(leftEtag) },
				{ "@leftSeriesMasterId", NullIfEmpty(leftSeriesMasterId) },
				{ "@rightEventId", NullIfEmpty(rightEventId) }, { "@rightEventKey", NullIfEmpty(rightEventKey) },
				{ "@rightICalUid", NullIfEmpty(rightICalUid) }, { "@rightEtag", NullIfEmpty(rightEtag) },
				{ "@rightRecurringEventId", NullIfEmpty(rightRecurringEventId) },
				{ "@unitKind", unitKind }, { "@originSeriesKey", NullIfEmpty(originSeriesKey) },
				{ "@occ", occurrenceOriginalStart.HasValue ? Common.makeMySqlDate(occurrenceOriginalStart.Value) : (object)DBNull.Value },
				{ "@projectedHash", NullIfEmpty(projectedHash) }, { "@originContentHash", NullIfEmpty(originContentHash) },
				{ "@status", status }, { "@mirrorVersion", mirrorVersion },
				{ "@lastSyncDirection", NullIfEmpty(lastSyncDirection) }
			};
			try
			{
				if (mappingID > 0)
				{
					p["@id"] = mappingID;
					string upd =
						"update event_mapping set originProvider=@originProvider, leftEventId=@leftEventId, leftEventKey=@leftEventKey, " +
						"leftICalUid=@leftICalUid, leftEtag=@leftEtag, leftSeriesMasterId=@leftSeriesMasterId, rightEventId=@rightEventId, " +
						"rightEventKey=@rightEventKey, rightICalUid=@rightICalUid, rightEtag=@rightEtag, rightRecurringEventId=@rightRecurringEventId, " +
						"unitKind=@unitKind, originSeriesKey=@originSeriesKey, occurrenceOriginalStart=@occ, projectedHash=@projectedHash, " +
						"originContentHash=@originContentHash, status=@status, mirrorVersion=@mirrorVersion, lastSyncDirection=@lastSyncDirection, " +
						"lastSyncedAt=NOW() where mappingID=@id";
					oDA.updateData(upd, p);
				}
				else
				{
					string ins =
						"insert into event_mapping (pairID, originProvider, leftEventId, leftEventKey, leftICalUid, leftEtag, leftSeriesMasterId, " +
						"rightEventId, rightEventKey, rightICalUid, rightEtag, rightRecurringEventId, unitKind, originSeriesKey, occurrenceOriginalStart, " +
						"projectedHash, originContentHash, status, mirrorVersion, lastSyncDirection, lastSyncedAt) values " +
						"(@pairID, @originProvider, @leftEventId, @leftEventKey, @leftICalUid, @leftEtag, @leftSeriesMasterId, @rightEventId, @rightEventKey, " +
						"@rightICalUid, @rightEtag, @rightRecurringEventId, @unitKind, @originSeriesKey, @occ, @projectedHash, @originContentHash, @status, " +
						"@mirrorVersion, @lastSyncDirection, NOW())";
					mappingID = oDA.insertData(ins, p);
				}
				errorMessage = oDA.errorMessage;
			}
			catch (Exception ex) { Common.writeToLog("ERROR EventMapping.save:", ex); }
			return mappingID;
		}

		public void tombstone()
		{
			if (mappingID <= 0) return;
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@id", mappingID } };
			oDA.updateData("update event_mapping set status='tombstoned', tombstonedAt=NOW() where mappingID=@id", p);
			status = "tombstoned";
		}

		// After recognizing a benign echo, refresh the mirror side's stored etag so
		// the next poll short-circuits on etag equality.
		public void refreshMirrorEtag(string mirrorSide, string etag)
		{
			if (mappingID <= 0) return;
			string col = mirrorSide == Providers.M365 ? "leftEtag" : "rightEtag";
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@e", etag ?? string.Empty }, { "@id", mappingID } };
			oDA.updateData($"update event_mapping set {col}=@e where mappingID=@id", p);
		}

		private static object NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? (object)DBNull.Value : s;

		private EventMapping dataRowToObject(DataRow r) => new()
		{
			mappingID = Common.ToLong(r["mappingID"]),
			pairID = Common.ToLong(r["pairID"]),
			originProvider = Common.ToStr(r["originProvider"]),
			leftEventId = Common.ToStr(r["leftEventId"]),
			leftEventKey = Common.ToStr(r["leftEventKey"]),
			leftICalUid = Common.ToStr(r["leftICalUid"]),
			leftEtag = Common.ToStr(r["leftEtag"]),
			leftSeriesMasterId = Common.ToStr(r["leftSeriesMasterId"]),
			rightEventId = Common.ToStr(r["rightEventId"]),
			rightEventKey = Common.ToStr(r["rightEventKey"]),
			rightICalUid = Common.ToStr(r["rightICalUid"]),
			rightEtag = Common.ToStr(r["rightEtag"]),
			rightRecurringEventId = Common.ToStr(r["rightRecurringEventId"]),
			unitKind = Common.ToStr(r["unitKind"]),
			originSeriesKey = Common.ToStr(r["originSeriesKey"]),
			occurrenceOriginalStart = Common.ToDateTimeUtc(r["occurrenceOriginalStart"]),
			projectedHash = Common.ToStr(r["projectedHash"]),
			originContentHash = Common.ToStr(r["originContentHash"]),
			status = Common.ToStr(r["status"]),
			mirrorVersion = Common.ToLong(r["mirrorVersion"]),
			lastSyncedAt = Common.ToDateTimeUtc(r["lastSyncedAt"]),
			lastSyncDirection = Common.ToStr(r["lastSyncDirection"]),
			tombstonedAt = Common.ToDateTimeUtc(r["tombstonedAt"])
		};
	}
}
