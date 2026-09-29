namespace Core.MTCalSync
{
	// The engine updates or deletes only mirrors it created. Anything else is someone's real
	// event: rewriting it can strip a meeting's guests, and deleting it can cancel the meeting
	// for every attendee.
	public static class MirrorGuard
	{
		// Null when `live` is a mirror the engine may write to or delete; otherwise the reason not to.
		// `originICalUid` is the iCalUID of the event the mirror copies, when the caller knows it.
		public static string? RefusalReason(RemoteEvent live, string? originICalUid = null)
		{
			if (!live.Stamp.Managed) return "not created by MT-CalSync";
			if (live.AttendeeCount > 0) return "has attendees";

			// A mirror is created fresh, so its provider assigns it a new iCalUID. An event that
			// shares the origin's iCalUID is another copy of the same meeting.
			string origin = GlobalObjectId.Normalize(string.IsNullOrEmpty(originICalUid) ? live.Stamp.OriginICalUid : originICalUid);
			if (origin.Length > 0 && string.Equals(GlobalObjectId.Normalize(live.ICalUid), origin, StringComparison.OrdinalIgnoreCase))
				return "a copy of the origin meeting, not a mirror";

			return null;
		}
	}
}
