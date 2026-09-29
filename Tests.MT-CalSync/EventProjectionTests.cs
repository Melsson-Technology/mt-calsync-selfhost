namespace Tests.MTCalSync
{
	// What a mirror is written with, per fidelity mode.
	public class EventProjectionTests
	{
		private static readonly DateTime Start = new(2026, 10, 6, 15, 0, 0, DateTimeKind.Utc);
		private static readonly RollingWindow Window = RollingWindow.Around(Start, 1, 60);

		private static RemoteEvent Event(bool isPrivate = false) => new()
		{
			Provider = Providers.M365, Id = "origin-1", ICalUid = "uid-1",
			StartUtc = Start, EndUtc = Start.AddHours(1), Subject = "Therapy", Location = "Suite 4",
			Body = "Bring the forms", AttendeeNames = new List<string> { "Pat Lee" }, AttendeeCount = 1,
			IsPrivate = isPrivate
		};

		private static ProjectedUnit Project(RemoteEvent src, SyncPair pair) =>
			Assert.Single(ProjectionFactory.For(pair).Project(src, pair, Window));

		[Fact]
		public void Full_detail_copies_title_location_and_notes()
		{
			var u = Project(Event(), new SyncPair { fidelityMode = Fidelity.FullDetail });
			Assert.Equal(("Therapy", "Suite 4", "Bring the forms"), (u.Subject, u.Location, u.Body));
		}

		[Fact]
		public void Attendee_names_go_into_the_notes_only_when_asked()
		{
			var off = Project(Event(), new SyncPair { fidelityMode = Fidelity.FullDetail });
			var on = Project(Event(), new SyncPair { fidelityMode = Fidelity.FullDetail, copyAttendeesToBody = true });
			Assert.DoesNotContain("Pat Lee", off.Body);
			Assert.Contains("Attendees: Pat Lee", on.Body);
		}

		[Fact]
		public void A_busy_block_carries_no_details()
		{
			var u = Project(Event(), new SyncPair { fidelityMode = Fidelity.BusyBlock });
			Assert.Equal(("Busy", "busy"), (u.Subject, u.ShowAs));
			Assert.Null(u.Location);
			Assert.Null(u.Body);
		}

		[Fact]
		public void A_busy_block_can_keep_the_title()
		{
			var u = Project(Event(), new SyncPair { fidelityMode = Fidelity.BusyBlock, copyTitle = true });
			Assert.Equal("Therapy", u.Subject);
		}

		[Theory]
		[InlineData(Fidelity.FullDetail)]
		[InlineData(Fidelity.BusyBlock)]
		public void A_private_event_is_projected_private_in_either_mode(string fidelity)
		{
			Assert.True(Project(Event(isPrivate: true), new SyncPair { fidelityMode = fidelity }).IsPrivate);
			Assert.False(Project(Event(), new SyncPair { fidelityMode = fidelity }).IsPrivate);
		}

		[Fact]
		public void The_hash_changes_with_the_content_and_with_privacy()
		{
			var pair = new SyncPair { fidelityMode = Fidelity.FullDetail };
			string baseline = Project(Event(), pair).Hash;
			var moved = Event(); moved.StartUtc = moved.StartUtc.AddHours(1);
			Assert.Equal(baseline, Project(Event(), pair).Hash);
			Assert.NotEqual(baseline, Project(moved, pair).Hash);
			Assert.NotEqual(baseline, Project(Event(isPrivate: true), pair).Hash);
		}

		// Mirrors store the hash they were written with. Changing what goes into the hash of an
		// ordinary event would make every existing mirror look stale and rewrite them all.
		[Fact]
		public void The_hash_of_an_event_that_is_not_private_keeps_its_format()
		{
			var u = new ProjectedUnit
			{
				UnitKind = UnitKinds.Single, StartUtc = Start, EndUtc = Start.AddHours(1), ShowAs = "busy",
				Subject = "Standup", Location = "Room 4", Body = "Notes"
			};
			string expected = Common.Sha256Hex($"single|{Start:O}|{Start.AddHours(1):O}|0|busy|Standup|Room 4|Notes|");
			Assert.Equal(expected, u.ComputeHash());
		}
	}
}
