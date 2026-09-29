namespace Core.MTCalSync
{
	// The only component that differs by fidelity mode. Turns one source event into the
	// units written to the mirror side; mapping, stamping and locking are the same in
	// every mode. Instance mode yields one unit per expanded occurrence, keyed by
	// (originSeriesKey, occurrenceOriginalStartUtc). Series mode yields a master with its
	// RRULE through the same interface.
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

	// Identity and recurrence linkage don't depend on fidelity.
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
				IsPrivate = src.IsPrivate,
				UnitKind = isOcc ? UnitKinds.Occurrence : UnitKinds.Single,
				OriginSeriesKey = seriesKey,
				OccurrenceOriginalStartUtc = src.OccurrenceOriginalStartUtc
			};
			return u;
		}
	}

	// The default mode: copies title, location, notes and times. Attendees are never copied
	// as guests, though their names can be appended to the body.
	public sealed class FullDetailProjection : ProjectionBase
	{
		public override string Kind => Fidelity.FullDetail;

		public override IReadOnlyList<ProjectedUnit> Project(RemoteEvent src, SyncPair pair, RollingWindow window)
		{
			var u = NewUnit(src);
			// In series mode a recurring master becomes one series_master unit with its RRULE.
			if (pair.SeriesMode && src.IsSeriesMaster)
			{
				u.UnitKind = UnitKinds.SeriesMaster;
				u.OriginSeriesKey = src.Id;
				u.OccurrenceOriginalStartUtc = null;
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

	// Busy block: "Busy" (or the title, if the pair copies it) and no other details.
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
