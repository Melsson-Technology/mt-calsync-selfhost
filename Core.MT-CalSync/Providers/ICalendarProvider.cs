namespace Core.MTCalSync
{
	// Result of a write to a provider.
	public class RemoteRef
	{
		public string Id { get; set; } = string.Empty;
		public string Etag { get; set; } = string.Empty;
		public string ICalUid { get; set; } = string.Empty;
	}

	// A calendar the connection's principal can see (calendar-discovery / picker).
	public class RemoteCalendar
	{
		public string Id { get; set; } = string.Empty;          // provider calendar id (Google: address / group id)
		public string Summary { get; set; } = string.Empty;     // display name (summaryOverride wins)
		public string? Description { get; set; }
		public string AccessRole { get; set; } = string.Empty;  // owner|writer|reader|freeBusyReader (Google)
		public bool Primary { get; set; }                        // the principal's own primary calendar
	}

	// Builds the mtcs_* provenance stamp written into every mirror event (Graph
	// singleValueExtendedProperties / Google extendedProperties.private).
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

	// One calendar endpoint we can read incrementally and write mirrors to. All
	// writes obey the hard safety rules: NO attendees on mirror events, and NO
	// notifications (Google sendUpdates=none; Graph has no attendees so nothing is sent).
	public interface ICalendarProvider
	{
		string Provider { get; }   // m365|google

		// Enumerate the calendars the principal can access (calendar picker). Google
		// returns the impersonated user's calendar list (primary + shared/subscribed);
		// Graph is a stub for now (destination is always the mailbox default calendar).
		Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync();

		// Incremental pull. Handles first-run/forceFull full sync and token expiry
		// (Graph re-mint on window drift; Google 410 -> full). Returns changed events
		// plus the new deltaLink/syncToken to persist AFTER a successful apply.
		Task<ChangeSet> GetChangesAsync(RollingWindow window, SyncState state, bool forceFull);

		// Targeted reads (repair / match-before-create ladder).
		Task<RemoteEvent?> GetAsync(string eventId);
		Task<RemoteEvent?> FindByStampAsync(long pairId, string originId);
		Task<RemoteEvent?> FindByICalUidAsync(string iCalUid);
		// Every event in this calendar stamped as pair `pairId`'s mirror, capped at
		// `max`. Support tier (stray-mirror sweep after a failed teardown) — the sync
		// loop never calls it.
		Task<IReadOnlyList<RemoteEvent>> ListByStampPairAsync(long pairId, int max);

		// Writes — always stamp provenance in the same call; never add attendees/guests.
		// A unit carrying a RecurrenceRule is written as a recurring master (series mode).
		Task<RemoteRef> CreateAsync(ProjectedUnit u, long pairId, long version);
		Task<RemoteRef> UpdateAsync(string eventId, string etag, ProjectedUnit u, long pairId, long version);
		Task DeleteAsync(string eventId, string etag);

		// Series-mode exception overrides: modify or cancel the single instance of the
		// mirror recurring master (mirrorMasterId) at the given original start.
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
