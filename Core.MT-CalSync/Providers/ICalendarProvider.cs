namespace Core.MTCalSync
{
	// Result of a write to a provider.
	public class RemoteRef
	{
		public string Id { get; set; } = string.Empty;
		public string Etag { get; set; } = string.Empty;
		public string ICalUid { get; set; } = string.Empty;
	}

	// A calendar the connection's principal can see.
	public class RemoteCalendar
	{
		public string Id { get; set; } = string.Empty;          // provider calendar id (Google: address / group id)
		public string Summary { get; set; } = string.Empty;     // display name (summaryOverride wins)
		public string? Description { get; set; }
		public string AccessRole { get; set; } = string.Empty;  // owner|writer|reader|freeBusyReader (Google)
		public bool Primary { get; set; }                        // the principal's own primary calendar
	}

	// The mtcs_* provenance stamp written into every mirror event (Graph
	// singleValueExtendedProperties, Google extendedProperties.private).
	public static class ProvenanceStamp
	{
		public static Dictionary<string, string> Build(ProjectedUnit u, long pairId, long version) => new()
		{
			{ StampKeys.Managed, "1" },
			{ StampKeys.Sys, u.OriginProvider },
			{ StampKeys.Oid, u.OriginId },
			{ StampKeys.Pair, pairId.ToString() },
			{ StampKeys.Hash, u.Hash },
			{ StampKeys.Ver, version.ToString() },
			{ StampKeys.Uid, u.OriginICalUid ?? string.Empty },
			{ StampKeys.AppId, Settings.AppInstanceId }
		};
	}

	// A calendar read incrementally and written with mirrors. Writes never add attendees
	// and never notify (Google sends sendUpdates=none; with no attendees Graph sends nothing).
	public interface ICalendarProvider
	{
		string Provider { get; }   // m365|google

		// The calendars the principal can access: the user's calendar list on Google,
		// the mailbox's calendars on Graph.
		Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync();

		// Changes since the stored token, or every event in `window` when forceFull is set or
		// there is no token; an expired token (410) falls back to a full list. The caller
		// decides when a token no longer covers the window (SyncEngine.TokenCovers) and
		// persists the returned token only after a successful apply.
		Task<ChangeSet> GetChangesAsync(RollingWindow window, SyncState state, bool forceFull);

		// Targeted reads for repair and match-before-create.
		Task<RemoteEvent?> GetAsync(string eventId);
		Task<RemoteEvent?> FindByStampAsync(long pairId, string originId);
		Task<RemoteEvent?> FindByICalUidAsync(string iCalUid);
		// Up to `max` events stamped as pair `pairId`'s mirrors. Used to sweep strays after
		// a failed teardown; the sync loop never calls it.
		Task<IReadOnlyList<RemoteEvent>> ListByStampPairAsync(long pairId, int max);

		// Writes stamp provenance in the same call and never add attendees.
		// A unit carrying a RecurrenceRule is written as a recurring master (series mode).
		Task<RemoteRef> CreateAsync(ProjectedUnit u, long pairId, long version);
		Task<RemoteRef> UpdateAsync(string eventId, string etag, ProjectedUnit u, long pairId, long version);
		Task DeleteAsync(string eventId, string etag);

		// Series mode: modify or cancel the mirror series' instance at the original start.
		Task<RemoteRef> UpsertInstanceAsync(string mirrorMasterId, DateTime originalStartUtc, ProjectedUnit u, long pairId, long version);
		Task CancelInstanceAsync(string mirrorMasterId, DateTime originalStartUtc);
	}

	// Raised when an item write fails. IsTransient drives retry/backoff vs dead-letter.
	public class ProviderException : Exception
	{
		public bool IsTransient { get; }
		public string Code { get; }
		public int? RetryAfterSeconds { get; }

		public ProviderException(string message, string code, bool isTransient, int? retryAfterSeconds = null, Exception? inner = null)
			: base(message, inner)
		{
			Code = code;
			IsTransient = isTransient;
			RetryAfterSeconds = retryAfterSeconds;
		}
	}
}
