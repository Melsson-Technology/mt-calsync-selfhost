using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util;
using System.Net;
using System.Text.Json;

namespace Core.MTCalSync
{
	// Google side: events.list with syncToken (whole/window-bounded, instance-
	// expanded), writes with sendUpdates=none and extendedProperties.private
	// (provenance). NEVER adds guests.
	//
	// Two credential shapes: service account + domain-wide delegation (operator
	// connections, impersonating a Workspace user) or a delegated per-user
	// UserCredential built from the owner's stored OAuth refresh token. On the
	// delegated path an invalid_grant refresh failure marks the grant needs_reauth
	// and surfaces NeedsReauthException (user problem, not a sync fault).
	public class GoogleCalendarProvider : ICalendarProvider
	{
		private readonly CalendarService _svc;
		private readonly string _calId;
		private readonly bool _seriesMode;
		private readonly long _oauthAccountId;   // 0 on the service-account path

		public string Provider => Providers.Google;

		public GoogleCalendarProvider(string serviceAccountJson, string impersonateEmail, string calendarId, bool seriesMode = false)
		{
			_seriesMode = seriesMode;
			// Build a ServiceAccountCredential straight from the key JSON and impersonate
			// the target user (domain-wide delegation) via the Initializer.User property —
			// the canonical, non-deprecated DWD pattern.
			using var doc = JsonDocument.Parse(serviceAccountJson);
			var root = doc.RootElement;
			string clientEmail = root.TryGetProperty("client_email", out var ce) ? (ce.GetString() ?? "") : "";
			string privateKey = root.TryGetProperty("private_key", out var pk) ? (pk.GetString() ?? "") : "";
			if (string.IsNullOrEmpty(clientEmail) || string.IsNullOrEmpty(privateKey))
				throw new InvalidOperationException("Google service-account JSON is missing client_email/private_key.");

			var credential = new ServiceAccountCredential(
				new ServiceAccountCredential.Initializer(clientEmail)
				{
					User = impersonateEmail,
					Scopes = new[] { CalendarService.Scope.Calendar }
				}.FromPrivateKey(privateKey));

			_svc = new CalendarService(new BaseClientService.Initializer
			{
				HttpClientInitializer = credential,
				ApplicationName = "MT-CalSync"
			});
			_calId = string.IsNullOrWhiteSpace(calendarId) ? "primary" : calendarId;
		}

		public GoogleCalendarProvider(UserCredential credential, long oauthAccountId, string calendarId, bool seriesMode = false)
		{
			_seriesMode = seriesMode;
			_oauthAccountId = oauthAccountId;
			_svc = new CalendarService(new BaseClientService.Initializer
			{
				HttpClientInitializer = credential,
				ApplicationName = "MT-CalSync"
			});
			_calId = string.IsNullOrWhiteSpace(calendarId) ? "primary" : calendarId;
		}

		// ── calendar discovery ──────────────────────────────────────────────
		// The impersonated user's calendar list (primary + calendars owned/shared/
		// subscribed). Reader access or better; the existing Scope.Calendar covers it.
		public async Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync()
		{
			var list = new List<RemoteCalendar>();
			try
			{
				string? pageToken = null;
				do
				{
					var req = _svc.CalendarList.List();
					req.MinAccessRole = CalendarListResource.ListRequest.MinAccessRoleEnum.Reader;
					req.ShowHidden = true;
					req.ShowDeleted = false;
					req.MaxResults = 250;
					if (pageToken != null) req.PageToken = pageToken;
					var res = await req.ExecuteAsync();
					if (res.Items != null)
						foreach (var e in res.Items)
							list.Add(new RemoteCalendar
							{
								Id = e.Id ?? string.Empty,
								Summary = e.SummaryOverride ?? e.Summary ?? e.Id ?? string.Empty,
								Description = e.Description,
								AccessRole = e.AccessRole ?? string.Empty,
								Primary = e.Primary ?? false
							});
					pageToken = res.NextPageToken;
				}
				while (!string.IsNullOrEmpty(pageToken));
			}
			catch (Exception ex) { throw Translate(ex, "google.calendarlist"); }
			return list;
		}

		// ── incremental pull ────────────────────────────────────────────────
		public async Task<ChangeSet> GetChangesAsync(RollingWindow window, SyncState state, bool forceFull)
		{
			var cs = new ChangeSet { WindowStart = window.StartUtc, WindowEnd = window.EndUtc };
			bool useIncremental = !forceFull && !string.IsNullOrEmpty(state.syncToken) && WindowStillCovered(state, window);
			cs.WasFullSync = !useIncremental;

			try
			{
				string? pageToken = null;
				string? nextSyncToken = null;
				do
				{
					var req = _svc.Events.List(_calId);
					// instance mode: expand recurrences; series mode: return masters + exceptions.
					// (Must be consistent across full/incremental for a given syncToken.)
					req.SingleEvents = !_seriesMode;
					req.ShowDeleted = true;       // include cancelled (deletion propagation)
					req.MaxResults = 2500;
					if (useIncremental)
					{
						req.SyncToken = state.syncToken;   // token remembers timeMin/timeMax; must NOT resend them
					}
					else
					{
						req.TimeMinDateTimeOffset = new DateTimeOffset(window.StartUtc, TimeSpan.Zero);
						req.TimeMaxDateTimeOffset = new DateTimeOffset(window.EndUtc, TimeSpan.Zero);
					}
					if (pageToken != null) req.PageToken = pageToken;

					Events evs = await req.ExecuteAsync();
					if (evs.Items != null)
						foreach (var e in evs.Items) cs.Items.Add(ToRemoteEvent(e));
					nextSyncToken = evs.NextSyncToken ?? nextSyncToken;
					pageToken = evs.NextPageToken;
				}
				while (!string.IsNullOrEmpty(pageToken));

				cs.NewToken = nextSyncToken;
			}
			catch (GoogleApiException gae) when (gae.HttpStatusCode == HttpStatusCode.Gone)
			{
				// syncToken expired (410) — full resync.
				Common.writeToLog("Google sync token 410 (Gone) — full resync.");
				return await GetChangesAsync(window, new SyncState { pairID = state.pairID, provider = Providers.Google }, true);
			}
			catch (Exception ex)
			{
				throw Translate(ex, "google.list");
			}
			return cs;
		}

		private static bool WindowStillCovered(SyncState state, RollingWindow window) =>
			state.windowEnd.HasValue && state.windowEnd.Value >= window.EndUtc.AddMinutes(-1);

		// ── targeted reads ──────────────────────────────────────────────────
		public async Task<RemoteEvent?> GetAsync(string eventId)
		{
			try { return ToRemoteEvent(await _svc.Events.Get(_calId, eventId).ExecuteAsync()); }
			catch (GoogleApiException gae) when (gae.HttpStatusCode == HttpStatusCode.NotFound || gae.HttpStatusCode == HttpStatusCode.Gone) { return null; }
			catch (Exception ex) { throw Translate(ex, "google.get"); }
		}

		public async Task<RemoteEvent?> FindByStampAsync(long pairId, string originId)
		{
			try
			{
				var req = _svc.Events.List(_calId);
				req.PrivateExtendedProperty = new Repeatable<string>(new[]
				{
					$"{StampKeys.Pair}={pairId}",
					$"{StampKeys.Oid}={originId}"
				});
				req.ShowDeleted = false;
				req.MaxResults = 5;
				var evs = await req.ExecuteAsync();
				var e = evs.Items?.FirstOrDefault();
				return e == null ? null : ToRemoteEvent(e);
			}
			catch (Exception ex) { Common.writeToLog("WARN FindByStampAsync (google): " + ex.Message); return null; }
		}

		public async Task<IReadOnlyList<RemoteEvent>> ListByStampPairAsync(long pairId, int max)
		{
			var strays = new List<RemoteEvent>();
			try
			{
				string? pageToken = null;
				do
				{
					var req = _svc.Events.List(_calId);
					req.PrivateExtendedProperty = new Repeatable<string>(new[] { $"{StampKeys.Pair}={pairId}" });
					req.ShowDeleted = false;
					req.MaxResults = Math.Min(250, max);
					req.PageToken = pageToken;
					var evs = await req.ExecuteAsync();
					foreach (var e in evs.Items ?? new List<Event>())
					{
						strays.Add(ToRemoteEvent(e));
						if (strays.Count >= max) return strays;
					}
					pageToken = evs.NextPageToken;
				} while (!string.IsNullOrEmpty(pageToken));
			}
			catch (Exception ex) { Common.writeToLog("WARN ListByStampPairAsync (google): " + ex.Message); }
			return strays;
		}

		public async Task<RemoteEvent?> FindByICalUidAsync(string iCalUid)
		{
			if (string.IsNullOrEmpty(iCalUid)) return null;
			try
			{
				var req = _svc.Events.List(_calId);
				req.ICalUID = iCalUid;
				req.ShowDeleted = false;
				req.MaxResults = 5;
				var evs = await req.ExecuteAsync();
				var e = evs.Items?.FirstOrDefault();
				return e == null ? null : ToRemoteEvent(e);
			}
			catch (Exception ex) { Common.writeToLog("WARN FindByICalUidAsync (google): " + ex.Message); return null; }
		}

		// ── writes ──────────────────────────────────────────────────────────
		public async Task<RemoteRef> CreateAsync(ProjectedUnit u, long pairId, long version)
		{
			var ev = BuildEvent(u, pairId, version);
			try
			{
				var req = _svc.Events.Insert(ev, _calId);
				req.SendUpdates = EventsResource.InsertRequest.SendUpdatesEnum.None;   // SAFETY: never notify
				req.SupportsAttachments = false;
				var created = await req.ExecuteAsync();
				return new RemoteRef { Id = created.Id ?? string.Empty, Etag = created.ETag ?? string.Empty, ICalUid = created.ICalUID ?? string.Empty };
			}
			catch (Exception ex) { throw Translate(ex, "google.create"); }
		}

		public async Task<RemoteRef> UpdateAsync(string eventId, string etag, ProjectedUnit u, long pairId, long version)
		{
			var ev = BuildEvent(u, pairId, version);
			try
			{
				var req = _svc.Events.Update(ev, _calId, eventId);
				req.SendUpdates = EventsResource.UpdateRequest.SendUpdatesEnum.None;
				var updated = await req.ExecuteAsync();
				return new RemoteRef { Id = updated.Id ?? eventId, Etag = updated.ETag ?? string.Empty, ICalUid = updated.ICalUID ?? string.Empty };
			}
			catch (Exception ex) { throw Translate(ex, "google.update"); }
		}

		public async Task DeleteAsync(string eventId, string etag)
		{
			try
			{
				var req = _svc.Events.Delete(_calId, eventId);
				req.SendUpdates = EventsResource.DeleteRequest.SendUpdatesEnum.None;
				await req.ExecuteAsync();
			}
			catch (GoogleApiException gae) when (gae.HttpStatusCode == HttpStatusCode.NotFound || gae.HttpStatusCode == HttpStatusCode.Gone) { /* already gone */ }
			catch (Exception ex) { throw Translate(ex, "google.delete"); }
		}

		// ── mapping helpers ─────────────────────────────────────────────────
		private Event BuildEvent(ProjectedUnit u, long pairId, long version)
		{
			var ev = new Event
			{
				Summary = u.Subject,
				Description = u.Body,
				Location = u.Location,
				Transparency = u.ShowAs == "free" ? "transparent" : "opaque",
				Start = ToGoogleDate(u.StartUtc, u.IsAllDay),
				End = ToGoogleDate(u.EndUtc, u.IsAllDay),
				// SAFETY: never set Attendees on a mirror event.
				ExtendedProperties = new Event.ExtendedPropertiesData
				{
					Private__ = new Dictionary<string, string>(ProvenanceStamp.Build(u, pairId, version))
				}
			};
			// Series master → carry the recurrence rule.
			if (u.UnitKind == UnitKinds.SeriesMaster && !string.IsNullOrWhiteSpace(u.RecurrenceRule))
				ev.Recurrence = new List<string> { "RRULE:" + u.RecurrenceRule };
			return ev;
		}

		private static string? ExtractRRule(IList<string> recurrence)
		{
			foreach (var line in recurrence)
				if (line.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
					return line.Substring(6);
			return null;
		}

		// ── series-mode instance overrides ──────────────────────────────────
		public async Task<RemoteRef> UpsertInstanceAsync(string mirrorMasterId, DateTime originalStartUtc, ProjectedUnit u, long pairId, long version)
		{
			try
			{
				var inst = await FindInstance(mirrorMasterId, originalStartUtc);
				if (inst == null) throw new ProviderException("google.instance: no instance at original start", "404", false);
				inst.Summary = u.Subject;
				inst.Description = u.Body;
				inst.Location = u.Location;
				inst.Transparency = u.ShowAs == "free" ? "transparent" : "opaque";
				inst.Start = ToGoogleDate(u.StartUtc, u.IsAllDay);
				inst.End = ToGoogleDate(u.EndUtc, u.IsAllDay);
				inst.Status = "confirmed";
				inst.ExtendedProperties = new Event.ExtendedPropertiesData   // provenance (inline echo + recovery)
				{
					Private__ = new Dictionary<string, string>(ProvenanceStamp.Build(u, pairId, version))
				};
				var req = _svc.Events.Update(inst, _calId, inst.Id);
				req.SendUpdates = EventsResource.UpdateRequest.SendUpdatesEnum.None;
				var updated = await req.ExecuteAsync();
				return new RemoteRef { Id = updated.Id ?? inst.Id, Etag = updated.ETag ?? string.Empty, ICalUid = updated.ICalUID ?? string.Empty };
			}
			catch (ProviderException) { throw; }
			catch (Exception ex) { throw Translate(ex, "google.instance-upsert"); }
		}

		public async Task CancelInstanceAsync(string mirrorMasterId, DateTime originalStartUtc)
		{
			try
			{
				var inst = await FindInstance(mirrorMasterId, originalStartUtc);
				if (inst == null) return;   // already gone
				var req = _svc.Events.Delete(_calId, inst.Id);
				req.SendUpdates = EventsResource.DeleteRequest.SendUpdatesEnum.None;
				await req.ExecuteAsync();
			}
			catch (GoogleApiException gae) when (gae.HttpStatusCode == HttpStatusCode.NotFound || gae.HttpStatusCode == HttpStatusCode.Gone) { }
			catch (Exception ex) { throw Translate(ex, "google.instance-cancel"); }
		}

		private async Task<Event?> FindInstance(string masterId, DateTime originalStartUtc)
		{
			var insts = _svc.Events.Instances(_calId, masterId);
			insts.OriginalStart = Common.ConvertDateTimeToRfc3339(originalStartUtc);
			insts.ShowDeleted = false;
			insts.MaxResults = 5;
			var res = await insts.ExecuteAsync();
			return res.Items?.FirstOrDefault();
		}

		private static EventDateTime ToGoogleDate(DateTime utc, bool allDay)
		{
			if (allDay)
				return new EventDateTime { Date = utc.ToString("yyyy-MM-dd") };
			return new EventDateTime
			{
				DateTimeDateTimeOffset = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)),
				TimeZone = "UTC"
			};
		}

		private RemoteEvent ToRemoteEvent(Event e)
		{
			bool cancelled = string.Equals(e.Status, "cancelled", StringComparison.OrdinalIgnoreCase);
			var re = new RemoteEvent
			{
				Provider = Providers.Google,
				Id = e.Id ?? string.Empty,
				ICalUid = e.ICalUID ?? string.Empty,
				Etag = e.ETag ?? string.Empty,
				IsDeleted = cancelled,
				Subject = e.Summary ?? string.Empty,
				Body = e.Description,
				Location = e.Location,
				SeriesMasterId = e.RecurringEventId,
				TimeZoneId = e.Start?.TimeZone ?? "UTC"
			};

			// Series master: has a recurrence and is not itself an instance.
			if (e.Recurrence != null && e.Recurrence.Count > 0 && string.IsNullOrEmpty(e.RecurringEventId))
			{
				re.IsSeriesMaster = true;
				re.RecurrenceRule = ExtractRRule(e.Recurrence);
			}

			// times
			if (e.Start?.Date != null)
			{
				re.IsAllDay = true;
				re.StartUtc = ParseDateOnly(e.Start.Date);
				re.EndUtc = ParseDateOnly(e.End?.Date);
			}
			else
			{
				re.IsAllDay = false;
				re.StartUtc = e.Start?.DateTimeDateTimeOffset?.UtcDateTime ?? DateTime.MinValue;
				re.EndUtc = e.End?.DateTimeDateTimeOffset?.UtcDateTime ?? re.StartUtc;
			}

			// show-as
			if (string.Equals(e.Transparency, "transparent", StringComparison.OrdinalIgnoreCase)) re.ShowAs = "free";
			else if (string.Equals(e.Status, "tentative", StringComparison.OrdinalIgnoreCase)) re.ShowAs = "tentative";
			else re.ShowAs = "busy";

			if (!string.IsNullOrEmpty(e.RecurringEventId))
				re.OccurrenceOriginalStartUtc = e.OriginalStartTime?.DateTimeDateTimeOffset?.UtcDateTime
					?? (e.OriginalStartTime?.Date != null ? ParseDateOnly(e.OriginalStartTime.Date) : re.StartUtc);

			if (e.Attendees != null)
				foreach (var a in e.Attendees)
					if (!string.IsNullOrWhiteSpace(a.DisplayName)) re.AttendeeNames.Add(a.DisplayName!);

			var map = new Dictionary<string, string>();
			if (e.ExtendedProperties?.Private__ != null)
				foreach (var kv in e.ExtendedProperties.Private__) map[kv.Key] = kv.Value;
			re.Stamp = Provenance.FromMap(map);
			return re;
		}

		private static DateTime ParseDateOnly(string? d)
		{
			if (string.IsNullOrEmpty(d)) return DateTime.MinValue;
			return DateTime.TryParse(d, out var dt) ? DateTime.SpecifyKind(dt.Date, DateTimeKind.Utc) : DateTime.MinValue;
		}

		private ProviderException Translate(Exception ex, string op)
		{
			// Delegated path: a refresh rejected with invalid_grant means the user's
			// grant is dead (revoked / expired consent) — flag the account and raise
			// the reauth signal instead of a retryable provider error.
			if (ex is Google.Apis.Auth.OAuth2.Responses.TokenResponseException tre)
			{
				if (_oauthAccountId > 0 && tre.Error?.Error == "invalid_grant")
				{
					new OAuthAccount().markNeedsReauth(_oauthAccountId, $"{tre.Error.Error}: {tre.Error.ErrorDescription}");
					throw new NeedsReauthException(_oauthAccountId, Providers.Google, "Refresh token rejected (invalid_grant).");
				}
				return new ProviderException($"{op}: token: {tre.Error?.Error}: {tre.Error?.ErrorDescription}",
					tre.Error?.Error ?? "token", isTransient: true, null, ex);
			}
			if (ex is GoogleApiException gae)
			{
				int code = (int)gae.HttpStatusCode;
				bool transient = code == 429 || code == 500 || code == 502 || code == 503 || code == 504;
				return new ProviderException($"{op}: HTTP {code}: {gae.Message}", code.ToString(), transient, null, ex);
			}
			return new ProviderException($"{op}: {ex.Message}", "transport", true, null, ex);
		}
	}
}
