using Microsoft.Graph.Models;

namespace Tests.MTCalSync
{
	// Series mode translates recurrence rules between Google (RFC 5545 RRULE) and Microsoft
	// Graph (PatternedRecurrence). Each rule here goes RRULE → Graph → RRULE and must come back
	// as it went in, which is the property the sync depends on: a series copied one way and
	// read back must not look changed.
	public class RecurrenceConverterTests
	{
		private static readonly DateTime Start = new(2026, 10, 6, 15, 0, 0, DateTimeKind.Utc);   // a Tuesday

		[Theory]
		[InlineData("FREQ=DAILY;INTERVAL=1")]
		[InlineData("FREQ=DAILY;INTERVAL=3;COUNT=10")]
		[InlineData("FREQ=WEEKLY;INTERVAL=1;BYDAY=TU;WKST=SU")]
		[InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE,FR;WKST=MO")]
		[InlineData("FREQ=WEEKLY;INTERVAL=1;BYDAY=TU,TH;WKST=SU;UNTIL=20261231T235959Z")]
		[InlineData("FREQ=MONTHLY;INTERVAL=1;BYMONTHDAY=15")]
		[InlineData("FREQ=MONTHLY;INTERVAL=1;BYDAY=2TU")]
		[InlineData("FREQ=MONTHLY;INTERVAL=1;BYDAY=-1FR")]
		[InlineData("FREQ=MONTHLY;INTERVAL=1;BYDAY=MO,TU;BYSETPOS=1")]
		[InlineData("FREQ=YEARLY;INTERVAL=1;BYMONTH=10;BYMONTHDAY=6")]
		[InlineData("FREQ=YEARLY;INTERVAL=1;BYMONTH=11;BYDAY=4TH")]
		public void A_rule_survives_the_round_trip(string rrule)
		{
			var graph = RecurrenceConverter.RRuleToGraph(rrule, Start, "America/Los_Angeles");
			Assert.NotNull(graph);
			Assert.Equal(rrule, RecurrenceConverter.GraphToRRule(graph));
		}

		[Fact]
		public void A_weekly_rule_without_BYDAY_repeats_on_the_start_day()
		{
			var graph = RecurrenceConverter.RRuleToGraph("FREQ=WEEKLY", Start, "UTC")!;
			Assert.Equal(new DayOfWeekObject?[] { DayOfWeekObject.Tuesday }, graph.Pattern!.DaysOfWeek);
		}

		[Fact]
		public void A_rule_with_no_end_has_no_end()
		{
			var graph = RecurrenceConverter.RRuleToGraph("FREQ=DAILY", Start, "UTC")!;
			Assert.Equal(RecurrenceRangeType.NoEnd, graph.Range!.Type);
		}

		[Theory]
		[InlineData("")]
		[InlineData("INTERVAL=2")]                 // no FREQ
		[InlineData("FREQ=HOURLY")]                // Graph has no hourly pattern
		[InlineData("FREQ=MONTHLY;INTERVAL=1")]    // monthly with neither a day nor a weekday
		public void A_rule_Graph_cannot_express_translates_to_nothing(string rrule) =>
			Assert.Null(RecurrenceConverter.RRuleToGraph(rrule, Start, "UTC"));

		[Fact]
		public void A_missing_Graph_recurrence_translates_to_nothing() =>
			Assert.Null(RecurrenceConverter.GraphToRRule(null));
	}
}
