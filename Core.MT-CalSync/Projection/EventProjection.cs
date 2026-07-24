namespace Core.MTCalSync
{
	// The ONLY component that differs by fidelity mode. Turns one source RemoteEvent
	// (an expanded occurrence or a single event) into 1..N ProjectedUnits — the
	// payload written to the mirror side. Engine/mapping/stamping/locking are
	// identical across modes.
	//
	// M1–M3 are instance-level: providers return expanded occurrences, so Project
	// returns exactly one unit per source event. Recurring instances carry a stable
	// (originSeriesKey, occurrenceOriginalStartUtc) so series-shrink can delete
	// mirrors whose source occurrence was cancelled/moved out of window. Series/RRULE
	// preservation (M4) would return a series_master unit instead — same interface.
	public interface IEventProjection
	{
		string Kind { get; }
		IReadOnlyList<ProjectedUnit> Project(RemoteEvent src, SyncPair pair, RollingWindow window);
	}

	public static class ProjectionFactory
	{
		public static IEventProjection For(SyncPair pair) => pair.fidelityMode == Fidelity.BusyBlock
			? new BusyBlockProjection()
			: new FullDetailProjection();
	}

	// Shared base: identity/recurrence linkage is fidelity-independent.
	public abstract class ProjectionBase : IEventProjection
	{
		public abstract string Kind { get; }
		public abstract IReadOnlyList<ProjectedUnit> Project(RemoteEvent src, SyncPair pair, RollingWindow window);

		protected static ProjectedUnit NewUnit(RemoteEvent src)
		{
			bool isOcc = src.IsRecurringInstance;
			string seriesKey = src.SeriesMasterId ?? string.Empty;
			var u = new ProjectedUnit
			{
				OriginProvider = src.Provider,
				OriginId = src.Id,
				OriginICalUid = src.ICalUid,
				StartUtc = src.StartUtc,
				EndUtc = src.EndUtc,
				TimeZoneId = string.IsNullOrWhiteSpace(src.TimeZoneId) ? "UTC" : src.TimeZoneId,
				IsAllDay = src.IsAllDay,
				ShowAs = string.IsNullOrWhiteSpace(src.ShowAs) ? "busy" : src.ShowAs,
				UnitKind = isOcc ? UnitKinds.Occurrence : UnitKinds.Single,
				OriginSeriesKey = seriesKey,
				OccurrenceOriginalStartUtc = src.OccurrenceOriginalStartUtc
			};
			u.UnitKey = isOcc && src.OccurrenceOriginalStartUtc.HasValue
				? $"{seriesKey}|{src.OccurrenceOriginalStartUtc.Value:O}"
				: src.Id;
			return u;
		}
	}

	// Full detail: copy title/location/notes/times. NEVER copies attendees as guests
	// (optionally appends their names to the body). This is the user's default mode.
	public sealed class FullDetailProjection : ProjectionBase
	{
		public override string Kind => Fidelity.FullDetail;

		public override IReadOnlyList<ProjectedUnit> Project(RemoteEvent src, SyncPair pair, RollingWindow window)
		{
			var u = NewUnit(src);
			// Series mode: a recurring master becomes a single series_master unit carrying
			// the RRULE (mirrored as one recurring event, not N occurrences).
			if (pair.SeriesMode && src.IsSeriesMaster)
			{
				u.UnitKind = UnitKinds.SeriesMaster;
				u.OriginSeriesKey = src.Id;
				u.OccurrenceOriginalStartUtc = null;
				u.UnitKey = src.Id;
				u.RecurrenceRule = src.RecurrenceRule;
			}
			u.Subject = string.IsNullOrWhiteSpace(src.Subject) ? "(no title)" : src.Subject;
			u.Location = src.Location;
			u.Body = src.Body;
			if (pair.copyAttendeesToBody && src.AttendeeNames.Count > 0)
			{
				string names = string.Join(", ", src.AttendeeNames);
				u.Body = string.IsNullOrWhiteSpace(u.Body) ? "Attendees: " + names : u.Body + "\n\nAttendees: " + names;
			}
			u.Hash = u.ComputeHash();
			return new[] { u };
		}
	}

	// Busy-block: opaque 'Busy' (or copied title), no details. Available per pair.
	public sealed class BusyBlockProjection : ProjectionBase
	{
		public override string Kind => Fidelity.BusyBlock;

		public override IReadOnlyList<ProjectedUnit> Project(RemoteEvent src, SyncPair pair, RollingWindow window)
		{
			var u = NewUnit(src);
			u.Subject = pair.copyTitle && !string.IsNullOrWhiteSpace(src.Subject) ? src.Subject : "Busy";
			u.ShowAs = "busy";
			u.Body = null;
			u.Location = null;
			u.Hash = u.ComputeHash();
			return new[] { u };
		}
	}
}
