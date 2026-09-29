namespace Tests.MTCalSync
{
	// The engine may update or delete only mirrors it created. Anything else is someone's real
	// event, and rewriting or deleting it can strip a meeting's guests or cancel it for everyone.
	public class MirrorGuardTests
	{
		private static RemoteEvent Mirror(string iCalUid = "uid-of-the-mirror", string originUid = "uid-of-the-origin", int attendees = 0) => new()
		{
			Id = "mirror-1", ICalUid = iCalUid, AttendeeCount = attendees,
			Stamp = new Provenance { Managed = true, OriginICalUid = originUid }
		};

		[Fact]
		public void A_stamped_mirror_with_no_attendees_may_be_written() =>
			Assert.Null(MirrorGuard.RefusalReason(Mirror()));

		[Fact]
		public void An_event_without_the_stamp_is_refused() =>
			Assert.NotNull(MirrorGuard.RefusalReason(new RemoteEvent { Id = "someones-meeting", ICalUid = "x" }));

		[Fact]
		public void A_mirror_someone_added_guests_to_is_refused() =>
			Assert.NotNull(MirrorGuard.RefusalReason(Mirror(attendees: 2)));

		[Fact]
		public void A_copy_sharing_the_origins_iCalUID_is_refused() =>
			Assert.NotNull(MirrorGuard.RefusalReason(Mirror(iCalUid: "shared-uid"), originICalUid: "shared-uid"));

		[Fact]
		public void The_iCalUID_comparison_sees_through_Outlooks_wrapping()
		{
			const string uid = "meeting-123@example.com";
			var copy = Mirror(iCalUid: GlobalObjectId.EncodeUid(uid), originUid: uid);
			Assert.NotNull(MirrorGuard.RefusalReason(copy));
		}

		[Fact]
		public void Without_a_caller_uid_the_stamps_origin_uid_is_used() =>
			Assert.NotNull(MirrorGuard.RefusalReason(Mirror(iCalUid: "same", originUid: "same")));
	}
}
