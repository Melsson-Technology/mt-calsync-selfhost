using System.Text;

namespace Core.MTCalSync
{
	// ── Provider identity ────────────────────────────────────────────────────
	// Left is always M365, right is always Google (see schema). The engine works in
	// terms of the source provider of a change and mirrors to the "other" side.
	public static class Providers
	{
		public const string M365 = "m365";
		public const string Google = "google";
		public static string Other(string p) => p == M365 ? Google : M365;
		public static bool IsValid(string p) => p == M365 || p == Google;
	}

	public static class Fidelity
	{
		public const string FullDetail = "full_detail";
		public const string BusyBlock = "busy_block";
	}

	// How ProviderFactory obtains credentials for a connection.
	public static class AuthKinds
	{
		public const string AppDefault = "app_default";        // global app credentials (operator connections)
		public const string DelegatedOauth = "delegated_oauth"; // per-user OAuth grant via oauth_account
	}

	public static class Directions
	{
		public const string Bidirectional = "bidirectional";
		public const string LeftToRight = "left_to_right";   // m365 -> google
		public const string RightToLeft = "right_to_left";   // google -> m365

		// Source providers whose native changes propagate, given a pair direction.
		public static IEnumerable<string> SourceSides(string direction) => direction switch
		{
			LeftToRight => new[] { Providers.M365 },
			RightToLeft => new[] { Providers.Google },
			_ => new[] { Providers.M365, Providers.Google }
		};
	}

	public static class UnitKinds
	{
		public const string Single = "single";
		public const string SeriesMaster = "series_master";
		public const string Occurrence = "occurrence";
	}

	public static class RecurrenceModes
	{
		public const string Instance = "instance";   // mirror each occurrence (M1–M3)
		public const string Series = "series";        // preserve masters + RRULE (M4)
	}

	// ── Provenance stamp keys (written into provider extended properties) ─────
	// Graph: singleValueExtendedProperties "String {namespaceGuid} Name <key>".
	// Google: extendedProperties.private["<key>"].
	public static class StampKeys
	{
		public const string Managed = "mtcs_managed";  // "1"
		public const string Sys = "mtcs_sys";          // origin system of the authoritative event
		public const string Oid = "mtcs_oid";          // origin event id
		public const string Pair = "mtcs_pair";        // sync_pair id
		public const string Hash = "mtcs_hash";        // projectedHash we wrote
		public const string Ver = "mtcs_ver";          // mirrorVersion we wrote
		public const string Uid = "mtcs_uid";          // origin iCalUId
		public const string AppId = "mtcs_appid";      // this app instance id

		public static readonly string[] All = { Managed, Sys, Oid, Pair, Hash, Ver, Uid, AppId };
	}

	// Parsed provenance stamp from a remote event (null-object friendly).
	public class Provenance
	{
		public bool Managed { get; set; }
		public string OriginSystem { get; set; } = string.Empty;
		public string OriginId { get; set; } = string.Empty;
		public long PairId { get; set; }
		public string Hash { get; set; } = string.Empty;
		public long Version { get; set; }
		public string OriginICalUid { get; set; } = string.Empty;
		public string AppId { get; set; } = string.Empty;

		// True when this event is a mirror WE wrote onto `polledProvider` (its origin
		// is the OTHER side) for this pair — i.e. an echo, not a native source change.
		public bool IsOurMirrorOn(string polledProvider, long pairId) =>
			Managed && PairId == pairId && OriginSystem.Length > 0 && OriginSystem != polledProvider;

		public static Provenance FromMap(IDictionary<string, string> m)
		{
			var p = new Provenance();
			if (m.TryGetValue(StampKeys.Managed, out var mg)) p.Managed = mg == "1";
			if (m.TryGetValue(StampKeys.Sys, out var s)) p.OriginSystem = s;
			if (m.TryGetValue(StampKeys.Oid, out var o)) p.OriginId = o;
			if (m.TryGetValue(StampKeys.Pair, out var pr) && long.TryParse(pr, out var pl)) p.PairId = pl;
			if (m.TryGetValue(StampKeys.Hash, out var h)) p.Hash = h;
			if (m.TryGetValue(StampKeys.Ver, out var v) && long.TryParse(v, out var vl)) p.Version = vl;
			if (m.TryGetValue(StampKeys.Uid, out var u)) p.OriginICalUid = u;
			if (m.TryGetValue(StampKeys.AppId, out var a)) p.AppId = a;
			return p;
		}
	}

	// ── Rolling window ───────────────────────────────────────────────────────
	public class RollingWindow
	{
		public DateTime StartUtc { get; set; }
		public DateTime EndUtc { get; set; }

		public static RollingWindow Around(DateTime nowUtc, int lookbackDays, int windowDays) => new()
		{
			StartUtc = nowUtc.AddDays(-Math.Abs(lookbackDays)),
			EndUtc = nowUtc.AddDays(Math.Abs(windowDays))
		};

		public bool Contains(DateTime utc) => utc >= StartUtc && utc <= EndUtc;
	}

	// ── RemoteEvent: normalized event read from either provider ───────────────
	public class RemoteEvent
	{
		public string Provider { get; set; } = string.Empty;   // m365|google
		public string Id { get; set; } = string.Empty;         // provider event id (occurrence id when expanded)
		public string ICalUid { get; set; } = string.Empty;
		public string Etag { get; set; } = string.Empty;
		public bool IsDeleted { get; set; }                    // removed / cancelled
		public bool IsAllDay { get; set; }
		public DateTime StartUtc { get; set; }
		public DateTime EndUtc { get; set; }
		public string TimeZoneId { get; set; } = "UTC";        // IANA
		public string Subject { get; set; } = string.Empty;
		public string? Body { get; set; }
		public string? Location { get; set; }
		public string ShowAs { get; set; } = "busy";           // busy|free|tentative|oof
		public string? SeriesMasterId { get; set; }            // Graph seriesMasterId / Google recurringEventId
		public DateTime? OccurrenceOriginalStartUtc { get; set; }
		public bool IsSeriesMaster { get; set; }               // series mode: this event IS a recurring master
		public string? RecurrenceRule { get; set; }            // RRULE (series master) — full-detail M4
		public List<string> AttendeeNames { get; set; } = new();
		public Provenance Stamp { get; set; } = new();         // parsed mtcs_* (may be unmanaged)

		// An exception/override instance of a series (has a master + an original start),
		// but NOT the master itself.
		public bool IsRecurringInstance => !IsSeriesMaster && (!string.IsNullOrEmpty(SeriesMasterId) || OccurrenceOriginalStartUtc.HasValue);
	}

	// ── ProjectedUnit: the payload written to the mirror side ─────────────────
	public class ProjectedUnit
	{
		public string UnitKey { get; set; } = string.Empty;    // originSeriesKey|occStartUtc  OR  originId
		public string UnitKind { get; set; } = UnitKinds.Single;
		public string OriginSeriesKey { get; set; } = string.Empty;
		public DateTime? OccurrenceOriginalStartUtc { get; set; }

		public DateTime StartUtc { get; set; }
		public DateTime EndUtc { get; set; }
		public string TimeZoneId { get; set; } = "UTC";
		public bool IsAllDay { get; set; }
		public string ShowAs { get; set; } = "busy";
		public string Subject { get; set; } = "Busy";
		public string? Body { get; set; }
		public string? Location { get; set; }
		public string? RecurrenceRule { get; set; }

		// Identity of the ORIGIN event (for stamping + mapping).
		public string OriginProvider { get; set; } = string.Empty;
		public string OriginId { get; set; } = string.Empty;
		public string OriginICalUid { get; set; } = string.Empty;

		public string Hash { get; set; } = string.Empty;

		// Canonical content hash of exactly what we mirror. Order-stable.
		public string ComputeHash()
		{
			var sb = new StringBuilder();
			sb.Append(UnitKind).Append('|')
			  .Append(StartUtc.ToString("O")).Append('|')
			  .Append(EndUtc.ToString("O")).Append('|')
			  .Append(IsAllDay ? '1' : '0').Append('|')
			  .Append(ShowAs).Append('|')
			  .Append(Subject).Append('|')
			  .Append(Location ?? string.Empty).Append('|')
			  .Append(Body ?? string.Empty).Append('|')
			  .Append(RecurrenceRule ?? string.Empty);
			return Common.Sha256Hex(sb.ToString());
		}
	}

	// ── ChangeSet: result of an incremental (or full) pull ───────────────────
	public class ChangeSet
	{
		public List<RemoteEvent> Items { get; set; } = new();
		public string? NewToken { get; set; }        // deltaLink (Graph) or nextSyncToken (Google)
		public bool WasFullSync { get; set; }
		public DateTime WindowStart { get; set; }
		public DateTime WindowEnd { get; set; }
	}
}
