using Microsoft.Graph.Models;
using MSDate = Microsoft.Kiota.Abstractions.Date;

namespace Core.MTCalSync
{
	// Bidirectional translation between Microsoft Graph's structured
	// PatternedRecurrence and an RFC 5545 RRULE string (the value Google Calendar
	// carries in event.Recurrence, minus the "RRULE:" prefix).
	//
	// This is the centerpiece of series-preserving (M4) sync and the deepest bug
	// surface in calendar interop. It is pure logic — fully unit-tested via round-trips.
	// Unsupported/exotic patterns return null; callers fall back gracefully.
	public static class RecurrenceConverter
	{
		private static readonly string[] RRuleDays = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

		// ── Graph → RRULE ────────────────────────────────────────────────────
		public static string? GraphToRRule(PatternedRecurrence? r)
		{
			if (r?.Pattern?.Type == null || r.Range == null) return null;
			var p = r.Pattern;
			int interval = p.Interval.GetValueOrDefault(1);
			var parts = new List<string>();

			switch (p.Type.Value)
			{
				case RecurrencePatternType.Daily:
					parts.Add("FREQ=DAILY");
					parts.Add($"INTERVAL={Math.Max(1, interval)}");
					break;

				case RecurrencePatternType.Weekly:
					parts.Add("FREQ=WEEKLY");
					parts.Add($"INTERVAL={Math.Max(1, interval)}");
					if (p.DaysOfWeek is { Count: > 0 }) parts.Add("BYDAY=" + string.Join(",", p.DaysOfWeek.Where(d => d.HasValue).Select(d => DayToken(d!.Value))));
					if (p.FirstDayOfWeek.HasValue) parts.Add("WKST=" + DayToken(p.FirstDayOfWeek.Value));
					break;

				case RecurrencePatternType.AbsoluteMonthly:
					parts.Add("FREQ=MONTHLY");
					parts.Add($"INTERVAL={Math.Max(1, interval)}");
					parts.Add($"BYMONTHDAY={p.DayOfMonth.GetValueOrDefault(1)}");
					break;

				case RecurrencePatternType.RelativeMonthly:
					parts.Add("FREQ=MONTHLY");
					parts.Add($"INTERVAL={Math.Max(1, interval)}");
					AddRelativeByDay(parts, p);
					break;

				case RecurrencePatternType.AbsoluteYearly:
					parts.Add("FREQ=YEARLY");
					parts.Add($"INTERVAL={Math.Max(1, interval)}");
					if (p.Month.HasValue) parts.Add($"BYMONTH={p.Month.Value}");
					parts.Add($"BYMONTHDAY={p.DayOfMonth.GetValueOrDefault(1)}");
					break;

				case RecurrencePatternType.RelativeYearly:
					parts.Add("FREQ=YEARLY");
					parts.Add($"INTERVAL={Math.Max(1, interval)}");
					if (p.Month.HasValue) parts.Add($"BYMONTH={p.Month.Value}");
					AddRelativeByDay(parts, p);
					break;

				default:
					return null;
			}

			// Range → COUNT / UNTIL.
			if (r.Range.Type == RecurrenceRangeType.Numbered && r.Range.NumberOfOccurrences.GetValueOrDefault() > 0)
				parts.Add($"COUNT={r.Range.NumberOfOccurrences!.Value}");
			else if (r.Range.Type == RecurrenceRangeType.EndDate && r.Range.EndDate != null)
			{
				var d = r.Range.EndDate.Value;
				parts.Add($"UNTIL={d.Year:D4}{d.Month:D2}{d.Day:D2}T235959Z");
			}

			return string.Join(";", parts);
		}

		private static void AddRelativeByDay(List<string> parts, RecurrencePattern p)
		{
			int ord = IndexToOrdinal(p.Index);
			var days = (p.DaysOfWeek ?? new List<DayOfWeekObject?>()).Where(d => d.HasValue).Select(d => DayToken(d!.Value)).ToList();
			if (days.Count == 0) return;
			if (days.Count == 1)
				parts.Add($"BYDAY={ord}{days[0]}");          // e.g. BYDAY=2TU / BYDAY=-1FR
			else
			{
				parts.Add("BYDAY=" + string.Join(",", days)); // multi-day: BYDAY=MO,TU + BYSETPOS
				parts.Add($"BYSETPOS={ord}");
			}
		}

		// ── RRULE → Graph ────────────────────────────────────────────────────
		public static PatternedRecurrence? RRuleToGraph(string? rrule, DateTime seriesStartUtc, string ianaTz)
		{
			if (string.IsNullOrWhiteSpace(rrule)) return null;
			var kv = ParseRRule(rrule);
			if (!kv.TryGetValue("FREQ", out var freq)) return null;

			var pattern = new RecurrencePattern { Interval = ParseInt(kv, "INTERVAL", 1) };
			var byDay = kv.TryGetValue("BYDAY", out var bd) ? bd.Split(',', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();

			switch (freq)
			{
				case "DAILY":
					pattern.Type = RecurrencePatternType.Daily;
					break;

				case "WEEKLY":
					pattern.Type = RecurrencePatternType.Weekly;
					pattern.DaysOfWeek = byDay.Length > 0
						? byDay.Select(TokenToDay).Where(d => d != null).Select(d => (DayOfWeekObject?)d!.Value).ToList()
						: new List<DayOfWeekObject?> { (DayOfWeekObject)((int)seriesStartUtc.DayOfWeek) };
					pattern.FirstDayOfWeek = kv.TryGetValue("WKST", out var wk) ? TokenToDay(wk) ?? DayOfWeekObject.Sunday : DayOfWeekObject.Sunday;
					break;

				case "MONTHLY":
					if (kv.TryGetValue("BYMONTHDAY", out var bmd) && int.TryParse(bmd, out var dom))
					{
						pattern.Type = RecurrencePatternType.AbsoluteMonthly;
						pattern.DayOfMonth = dom;
					}
					else if (byDay.Length > 0)
					{
						pattern.Type = RecurrencePatternType.RelativeMonthly;
						ApplyRelativeByDay(pattern, byDay, kv);
					}
					else return null;
					break;

				case "YEARLY":
					pattern.Month = ParseInt(kv, "BYMONTH", seriesStartUtc.Month);
					if (kv.TryGetValue("BYMONTHDAY", out var ybmd) && int.TryParse(ybmd, out var ydom))
					{
						pattern.Type = RecurrencePatternType.AbsoluteYearly;
						pattern.DayOfMonth = ydom;
					}
					else if (byDay.Length > 0)
					{
						pattern.Type = RecurrencePatternType.RelativeYearly;
						ApplyRelativeByDay(pattern, byDay, kv);
					}
					else
					{
						pattern.Type = RecurrencePatternType.AbsoluteYearly;
						pattern.DayOfMonth = seriesStartUtc.Day;
					}
					break;

				default:
					return null;
			}

			var range = new RecurrenceRange
			{
				StartDate = new MSDate(seriesStartUtc.Year, seriesStartUtc.Month, seriesStartUtc.Day),
				RecurrenceTimeZone = IanaToWindows(ianaTz)
			};
			if (kv.TryGetValue("COUNT", out var cnt) && int.TryParse(cnt, out var c))
			{
				range.Type = RecurrenceRangeType.Numbered;
				range.NumberOfOccurrences = c;
			}
			else if (kv.TryGetValue("UNTIL", out var until) && TryParseUntil(until, out var untilDate))
			{
				range.Type = RecurrenceRangeType.EndDate;
				range.EndDate = new MSDate(untilDate.Year, untilDate.Month, untilDate.Day);
			}
			else
			{
				range.Type = RecurrenceRangeType.NoEnd;
			}

			return new PatternedRecurrence { Pattern = pattern, Range = range };
		}

		private static void ApplyRelativeByDay(RecurrencePattern pattern, string[] byDay, Dictionary<string, string> kv)
		{
			// BYDAY tokens may carry an ordinal ("2TU", "-1FR"); otherwise BYSETPOS holds it.
			int ordinal = 1;
			var days = new List<DayOfWeekObject?>();
			foreach (var tok in byDay)
			{
				var (ord, day) = SplitOrdinalDay(tok);
				if (ord.HasValue) ordinal = ord.Value;
				var d = TokenToDay(day);
				if (d != null) days.Add(d.Value);
			}
			if (kv.TryGetValue("BYSETPOS", out var sp) && int.TryParse(sp, out var setpos)) ordinal = setpos;
			pattern.DaysOfWeek = days;
			pattern.Index = OrdinalToIndex(ordinal);
		}

		// ── helpers ──────────────────────────────────────────────────────────
		private static string DayToken(DayOfWeekObject d) => d switch
		{
			DayOfWeekObject.Sunday => "SU",
			DayOfWeekObject.Monday => "MO",
			DayOfWeekObject.Tuesday => "TU",
			DayOfWeekObject.Wednesday => "WE",
			DayOfWeekObject.Thursday => "TH",
			DayOfWeekObject.Friday => "FR",
			DayOfWeekObject.Saturday => "SA",
			_ => "MO"
		};

		private static DayOfWeekObject? TokenToDay(string tok)
		{
			int i = Array.IndexOf(RRuleDays, tok.Trim().ToUpperInvariant());
			return i >= 0 ? (DayOfWeekObject)i : null;
		}

		private static (int? ord, string day) SplitOrdinalDay(string token)
		{
			token = token.Trim().ToUpperInvariant();
			// Trailing 2 chars are the weekday; any leading sign+digits are the ordinal.
			if (token.Length < 2) return (null, token);
			string day = token[^2..];
			string prefix = token[..^2];
			if (prefix.Length == 0) return (null, day);
			return int.TryParse(prefix, out var ord) ? (ord, day) : (null, day);
		}

		private static int IndexToOrdinal(WeekIndex? idx) => idx switch
		{
			WeekIndex.First => 1,
			WeekIndex.Second => 2,
			WeekIndex.Third => 3,
			WeekIndex.Fourth => 4,
			WeekIndex.Last => -1,
			_ => 1
		};

		private static WeekIndex OrdinalToIndex(int ord) => ord switch
		{
			1 => WeekIndex.First,
			2 => WeekIndex.Second,
			3 => WeekIndex.Third,
			4 => WeekIndex.Fourth,
			-1 => WeekIndex.Last,
			>= 5 => WeekIndex.Last,
			_ => WeekIndex.First
		};

		private static Dictionary<string, string> ParseRRule(string rrule)
		{
			var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var part in rrule.Replace("RRULE:", "", StringComparison.OrdinalIgnoreCase).Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				int eq = part.IndexOf('=');
				if (eq > 0) kv[part[..eq].Trim().ToUpperInvariant()] = part[(eq + 1)..].Trim();
			}
			return kv;
		}

		private static int ParseInt(Dictionary<string, string> kv, string key, int def) =>
			kv.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : def;

		private static bool TryParseUntil(string until, out DateTime dt)
		{
			until = until.Trim().TrimEnd('Z');
			// Accept yyyyMMdd or yyyyMMddTHHmmss.
			string datePart = until.Length >= 8 ? until[..8] : until;
			if (DateTime.TryParseExact(datePart, "yyyyMMdd", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out dt))
				return true;
			return DateTime.TryParse(until, out dt);
		}

		// Graph's RecurrenceTimeZone historically wants a Windows tz name.
		private static string IanaToWindows(string iana)
		{
			if (string.IsNullOrWhiteSpace(iana) || iana == "UTC") return "UTC";
			return TimeZoneInfo.TryConvertIanaIdToWindowsId(iana, out var win) && win != null ? win : iana;
		}
	}
}
