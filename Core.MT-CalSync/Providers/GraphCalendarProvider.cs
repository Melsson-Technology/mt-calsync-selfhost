using System.Globalization;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;

namespace Core.MTCalSync
{
	// Microsoft 365 provider. Reads calendarView/delta (windowed, instance-expanded);
	// writes carry a transactionId (idempotent create) and the provenance stamp as
	// singleValueExtendedProperties. Never adds attendees, so Exchange sends no invitations.
	//
	// Credentials are either app-only (principal = mailbox UPN) or delegated
	// (principal = the user's Entra object id; /users/{own-oid} behaves like /me).
	// A non-default calendarId routes list and create calls through Calendars[id];
	// by-id operations are mailbox-scoped and need no routing.
	public class GraphCalendarProvider : ICalendarProvider
	{
		private readonly GraphServiceClient _graph;
		private readonly string _principal;
		private readonly string _calId;
		private readonly string _ns;   // extended-property namespace guid
		private readonly bool _seriesMode;

		public string Provider => Providers.M365;

		public GraphCalendarProvider(string tenantId, string clientId, string clientSecret, string principalEmail, string calendarId = "primary", bool seriesMode = false)
			: this(new ClientSecretCredential(tenantId, clientId, clientSecret) as Azure.Core.TokenCredential, principalEmail, calendarId, seriesMode)
		{
		}

		public GraphCalendarProvider(Azure.Core.TokenCredential credential, string principal, string calendarId = "primary", bool seriesMode = false)
		{
			_graph = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
			_principal = principal;
			_calId = calendarId ?? string.Empty;
			_ns = Settings.ExtPropNamespaceGuid;
			_seriesMode = seriesMode;
		}

		private bool UseDefaultCalendar =>
			string.IsNullOrWhiteSpace(_calId) || _calId == "primary" || _calId == "default";

		// Calendar discovery
		public async Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync()
		{
			var list = new List<RemoteCalendar>();
			try
			{
				var resp = await _graph.Users[_principal].Calendars.GetAsync(rc => { rc.QueryParameters.Top = 100; });
				while (resp != null)
				{
					if (resp.Value != null)
						foreach (var c in resp.Value)
							list.Add(new RemoteCalendar
							{
								Id = c.Id ?? string.Empty,
								Summary = c.Name ?? c.Id ?? string.Empty,
								Primary = c.IsDefaultCalendar ?? false,
								AccessRole = (c.CanEdit ?? false) ? "writer" : "reader"
							});
					if (string.IsNullOrEmpty(resp.OdataNextLink)) break;
					resp = await _graph.Users[_principal].Calendars.WithUrl(resp.OdataNextLink).GetAsync();
				}
			}
			catch (Exception ex) { throw Translate(ex, "graph.list-calendars"); }
			return list;
		}

		// Incremental pull
		public async Task<ChangeSet> GetChangesAsync(RollingWindow window, SyncState state, bool forceFull)
		{
			var cs = new ChangeSet();
			var byId = new Dictionary<string, Event>();
			int maxPages = Settings.MaxDeltaPages;
			int pages = 0;
			string? deltaLink = null;
			// A deltaLink stays bound to the window it was minted over; the engine re-mints
			// (forceFull) once the live window outgrows it.
			bool useIncremental = !forceFull && !string.IsNullOrEmpty(state.deltaLink);

			try
			{
				// FetchDeltaPage handles default vs named calendar routing.
				var page = await FetchDeltaPage(useIncremental ? state.deltaLink : null, window);
				cs.WasFullSync = !useIncremental;

				while (true)
				{
					foreach (var ev in page.Items)
						if (!string.IsNullOrEmpty(ev.Id)) byId[ev.Id] = ev;   // collapse-by-id (last wins)

					if (!string.IsNullOrEmpty(page.DeltaLink)) { deltaLink = page.DeltaLink; break; }
					if (string.IsNullOrEmpty(page.NextLink)) break;
					if (++pages >= maxPages)
					{
						// Guards against runaway recurrence expansion: drop the token so
						// the next run does a full resync.
						Common.writeToLog($"Graph delta exceeded {maxPages} pages — abandoning token, forcing full resync next run.");
						deltaLink = null;
						break;
					}
					page = await FetchDeltaPage(page.NextLink, null);
				}
			}
			catch (ApiException ax) when (ax.ResponseStatusCode == 410)
			{
				// Expired delta token: full resync.
				Common.writeToLog("Graph delta 410 (token expired) — full resync.");
				return await GetChangesAsync(window, new SyncState { pairID = state.pairID, provider = Providers.M365 }, true);
			}
			catch (Exception ex)
			{
				throw Translate(ex, "graph.delta");
			}

			if (_seriesMode)
			{
				// calendarView/delta always expands recurrences, so masters never appear.
				// Fetch each changed occurrence's master once (it carries the rule); plain
				// occurrences are skipped because the mirrored master covers them.
				var masterIds = new HashSet<string>();
				var removedMasterIds = new HashSet<string>();
				foreach (var ev in byId.Values)
				{
					bool removed = ev.AdditionalData != null && ev.AdditionalData.ContainsKey("@removed");
					if (removed)
					{
						if (!string.IsNullOrEmpty(ev.SeriesMasterId)) removedMasterIds.Add(ev.SeriesMasterId!);
						else cs.Items.Add(ToRemoteEvent(ev));   // a removed single event
						continue;
					}
					if (ev.Type == EventType.Occurrence)
					{
						if (!string.IsNullOrEmpty(ev.SeriesMasterId)) masterIds.Add(ev.SeriesMasterId!);
					}
					else if (ev.Type == EventType.Exception)
					{
						cs.Items.Add(ToRemoteEvent(ev));   // modified single occurrence (override)
						if (!string.IsNullOrEmpty(ev.SeriesMasterId)) masterIds.Add(ev.SeriesMasterId!);
					}
					else
					{
						cs.Items.Add(ToRemoteEvent(ev));   // singleInstance (or unknown)
					}
				}
				foreach (var mid in masterIds)
				{
					try { var master = await GetAsync(mid); if (master != null) cs.Items.Add(master); }
					catch (ProviderException ex) when (!ex.IsTransient) { Common.writeToLog("WARN fetch series master " + mid + ": " + ex.Message); }
				}
				// A removed occurrence whose master no longer exists means the series was
				// deleted: emit a deleted master so the mirror series is removed.
				foreach (var mid in removedMasterIds)
				{
					if (masterIds.Contains(mid)) continue;
					try { if (await GetAsync(mid) == null) cs.Items.Add(new RemoteEvent { Provider = Providers.M365, Id = mid, IsDeleted = true }); }
					catch (ProviderException ex) when (!ex.IsTransient) { Common.writeToLog("WARN check removed master " + mid + ": " + ex.Message); }
				}
			}
			else
			{
				foreach (var ev in byId.Values)
				{
					var re = ToRemoteEvent(ev);
					// Delta returns unmodified occurrences without subject or iCalUId and with
					// isAllDay false. Mirrored as-is they become untitled timed events, and the
					// missing uid defeats iCalUId adoption, so re-read the full occurrence.
					if (!re.IsDeleted && re.IsRecurringInstance && !string.IsNullOrEmpty(re.Id)
						&& (string.IsNullOrEmpty(re.ICalUid) || string.IsNullOrEmpty(re.Subject)))
					{
						try { var full = await GetAsync(re.Id); if (full != null) re = full; }
						catch (ProviderException ex) when (!ex.IsTransient) { Common.writeToLog("WARN enrich occurrence " + re.Id + ": " + ex.Message); }
					}
					cs.Items.Add(re);
				}
			}
			cs.NewToken = deltaLink;
			return cs;
		}

		// One delta page. The SDK returns a distinct response type for default and named
		// calendars; this keeps the paging loop to one shape.
		private sealed class DeltaPage
		{
			public List<Event> Items { get; } = new();
			public string? DeltaLink { get; set; }
			public string? NextLink { get; set; }
		}

		// A null url starts a windowed request; otherwise the nextLink/deltaLink is followed
		// as-is. Every page sends the UTC Prefer header: ParseGraphDate assumes UTC, and a
		// link keeps its query but not its headers.
		private async Task<DeltaPage> FetchDeltaPage(string? url, RollingWindow? window)
		{
			var page = new DeltaPage();
			if (UseDefaultCalendar)
			{
				var r = url != null
					? await _graph.Users[_principal].CalendarView.Delta.WithUrl(url).GetAsDeltaGetResponseAsync(rc => rc.Headers.Add("Prefer", "outlook.timezone=\"UTC\""))
					: await _graph.Users[_principal].CalendarView.Delta.GetAsDeltaGetResponseAsync(rc =>
					{
						rc.QueryParameters.StartDateTime = window!.StartUtc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
						rc.QueryParameters.EndDateTime = window.EndUtc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
						rc.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
					});
				if (r?.Value != null) page.Items.AddRange(r.Value);
				page.DeltaLink = r?.OdataDeltaLink;
				page.NextLink = r?.OdataNextLink;
			}
			else
			{
				var r = url != null
					? await _graph.Users[_principal].Calendars[_calId].CalendarView.Delta.WithUrl(url).GetAsDeltaGetResponseAsync(rc => rc.Headers.Add("Prefer", "outlook.timezone=\"UTC\""))
					: await _graph.Users[_principal].Calendars[_calId].CalendarView.Delta.GetAsDeltaGetResponseAsync(rc =>
					{
						rc.QueryParameters.StartDateTime = window!.StartUtc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
						rc.QueryParameters.EndDateTime = window.EndUtc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
						rc.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
					});
				if (r?.Value != null) page.Items.AddRange(r.Value);
				page.DeltaLink = r?.OdataDeltaLink;
				page.NextLink = r?.OdataNextLink;
			}
			return page;
		}

		// Targeted reads
		public async Task<RemoteEvent?> GetAsync(string eventId)
		{
			try
			{
				var ev = await _graph.Users[_principal].Events[eventId].GetAsync(rc =>
				{
					rc.QueryParameters.Expand = new[] { StampExpand() };
					rc.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
				});
				return ev == null ? null : ToRemoteEvent(ev);
			}
			catch (ApiException ax) when (ax.ResponseStatusCode == 404) { return null; }
			catch (Exception ex) { throw Translate(ex, "graph.get"); }
		}

		public async Task<RemoteEvent?> FindByStampAsync(long pairId, string originId)
		{
			string filter =
				$"singleValueExtendedProperties/any(ep: ep/id eq 'String {{{_ns}}} Name {StampKeys.Oid}' and ep/value eq '{Escape(originId)}')";
			try
			{
				// The events list endpoint is calendar-scoped, unlike by-id operations.
				List<Event>? found;
				if (UseDefaultCalendar)
				{
					var resp = await _graph.Users[_principal].Events.GetAsync(rc =>
					{
						rc.QueryParameters.Filter = filter;
						rc.QueryParameters.Expand = new[] { StampExpand() };
						rc.QueryParameters.Top = 5;
						rc.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
					});
					found = resp?.Value;
				}
				else
				{
					var resp = await _graph.Users[_principal].Calendars[_calId].Events.GetAsync(rc =>
					{
						rc.QueryParameters.Filter = filter;
						rc.QueryParameters.Expand = new[] { StampExpand() };
						rc.QueryParameters.Top = 5;
						rc.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
					});
					found = resp?.Value;
				}
				var ev = found?.FirstOrDefault(e => StampPairMatches(e, pairId));
				return ev == null ? null : ToRemoteEvent(ev);
			}
			catch (Exception ex) { ThrowIfTransient(ex, "graph.FindByStampAsync"); Common.writeToLog("WARN FindByStampAsync (graph): " + ex.Message); return null; }
		}

		public async Task<IReadOnlyList<RemoteEvent>> ListByStampPairAsync(long pairId, int max)
		{
			string filter =
				$"singleValueExtendedProperties/any(ep: ep/id eq 'String {{{_ns}}} Name {StampKeys.Pair}' and ep/value eq '{pairId}')";
			var strays = new List<RemoteEvent>();
			try
			{
				// One capped page: stray sweeps deal in dozens, not thousands.
				List<Event>? found;
				if (UseDefaultCalendar)
				{
					var resp = await _graph.Users[_principal].Events.GetAsync(rc =>
					{
						rc.QueryParameters.Filter = filter;
						rc.QueryParameters.Expand = new[] { StampExpand() };
						rc.QueryParameters.Top = Math.Min(250, max);
						rc.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
					});
					found = resp?.Value;
				}
				else
				{
					var resp = await _graph.Users[_principal].Calendars[_calId].Events.GetAsync(rc =>
					{
						rc.QueryParameters.Filter = filter;
						rc.QueryParameters.Expand = new[] { StampExpand() };
						rc.QueryParameters.Top = Math.Min(250, max);
						rc.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
					});
					found = resp?.Value;
				}
				foreach (var e in found ?? new List<Event>())
				{
					if (strays.Count >= max) break;
					strays.Add(ToRemoteEvent(e));
				}
			}
			catch (Exception ex) { ThrowIfTransient(ex, "graph.ListByStampPairAsync"); Common.writeToLog("WARN ListByStampPairAsync (graph): " + ex.Message); }
			return strays;
		}

		public async Task<RemoteEvent?> FindByICalUidAsync(string iCalUid)
		{
			if (string.IsNullOrEmpty(iCalUid)) return null;
			try
			{
				// Graph filters on the raw GlobalObjectId hex, but we store the clean RFC UID.
				// Query both forms so an externally organised invite still matches.
				var forms = new List<string> { iCalUid };
				if (GlobalObjectId.TryExtractUid(iCalUid) == null)   // iCalUid is clean, not itself a GOID
				{
					string goid = GlobalObjectId.EncodeUid(iCalUid);
					if (!string.IsNullOrEmpty(goid) && goid != iCalUid) forms.Add(goid);
				}
				string filter = string.Join(" or ", forms.Select(f => $"iCalUId eq '{Escape(f)}'"));
				List<Event>? found;
				if (UseDefaultCalendar)
				{
					var resp = await _graph.Users[_principal].Events.GetAsync(rc =>
					{
						rc.QueryParameters.Filter = filter;
						rc.QueryParameters.Top = 5;
						rc.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
					});
					found = resp?.Value;
				}
				else
				{
					var resp = await _graph.Users[_principal].Calendars[_calId].Events.GetAsync(rc =>
					{
						rc.QueryParameters.Filter = filter;
						rc.QueryParameters.Top = 5;
						rc.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
					});
					found = resp?.Value;
				}
				var ev = found?.FirstOrDefault();
				return ev == null ? null : ToRemoteEvent(ev);
			}
			catch (Exception ex) { ThrowIfTransient(ex, "graph.FindByICalUidAsync"); Common.writeToLog("WARN FindByICalUidAsync (graph): " + ex.Message); return null; }
		}

		// Writes
		public async Task<RemoteRef> CreateAsync(ProjectedUnit u, long pairId, long version)
		{
			var ev = BuildEvent(u, pairId, version);
			// Idempotent create: a retried POST with the same transactionId won't duplicate.
			ev.TransactionId = $"mtcs-{pairId}-{Common.Sha256Hex(u.OriginProvider + "|" + u.OriginId).Substring(0, 24)}";
			try
			{
				var created = UseDefaultCalendar
					? await _graph.Users[_principal].Events.PostAsync(ev)
					: await _graph.Users[_principal].Calendars[_calId].Events.PostAsync(ev);
				return new RemoteRef { Id = created?.Id ?? string.Empty, Etag = ReadEtag(created), ICalUid = created?.ICalUId ?? string.Empty };
			}
			catch (Exception ex) { throw Translate(ex, "graph.create"); }
		}

		public async Task<RemoteRef> UpdateAsync(string eventId, string etag, ProjectedUnit u, long pairId, long version)
		{
			var ev = BuildEvent(u, pairId, version);
			try
			{
				var updated = await _graph.Users[_principal].Events[eventId].PatchAsync(ev);
				return new RemoteRef { Id = eventId, Etag = ReadEtag(updated), ICalUid = updated?.ICalUId ?? string.Empty };
			}
			catch (Exception ex) { throw Translate(ex, "graph.update"); }
		}

		public async Task DeleteAsync(string eventId, string etag)
		{
			try { await _graph.Users[_principal].Events[eventId].DeleteAsync(); }
			catch (ApiException ax) when (ax.ResponseStatusCode == 404) { /* already gone */ }
			catch (Exception ex) { throw Translate(ex, "graph.delete"); }
		}

		// Mapping helpers
		private Event BuildEvent(ProjectedUnit u, long pairId, long version)
		{
			var ev = new Event
			{
				Subject = u.Subject,
				Body = new ItemBody { ContentType = BodyType.Text, Content = u.Body ?? string.Empty },
				IsAllDay = u.IsAllDay,
				ShowAs = MapShowAsToGraph(u.ShowAs),
				Sensitivity = u.IsPrivate ? Sensitivity.Private : Sensitivity.Normal,
				Location = string.IsNullOrEmpty(u.Location) ? null : new Location { DisplayName = u.Location },
				Start = ToGraphDateTime(u.StartUtc, u.IsAllDay),
				End = ToGraphDateTime(u.EndUtc, u.IsAllDay),
				// Never set Attendees on a mirror event.
				SingleValueExtendedProperties = BuildStampProps(u, pairId, version)
			};
			// A series master carries its RRULE as Graph's PatternedRecurrence.
			if (u.UnitKind == UnitKinds.SeriesMaster && !string.IsNullOrWhiteSpace(u.RecurrenceRule))
				ev.Recurrence = RecurrenceConverter.RRuleToGraph(u.RecurrenceRule, u.StartUtc, u.TimeZoneId);
			return ev;
		}

		// Series-mode instance overrides
		public async Task<RemoteRef> UpsertInstanceAsync(string mirrorMasterId, DateTime originalStartUtc, ProjectedUnit u, long pairId, long version)
		{
			try
			{
				var inst = await FindInstance(mirrorMasterId, originalStartUtc);
				if (inst?.Id == null) throw new ProviderException("graph.instance: no instance at original start", "404", false);
				var patch = new Event
				{
					Subject = u.Subject,
					Body = new ItemBody { ContentType = BodyType.Text, Content = u.Body ?? string.Empty },
					Location = string.IsNullOrEmpty(u.Location) ? null : new Location { DisplayName = u.Location },
					ShowAs = MapShowAsToGraph(u.ShowAs),
					Sensitivity = u.IsPrivate ? Sensitivity.Private : Sensitivity.Normal,
					Start = ToGraphDateTime(u.StartUtc, u.IsAllDay),
					End = ToGraphDateTime(u.EndUtc, u.IsAllDay),
					SingleValueExtendedProperties = BuildStampProps(u, pairId, version)   // provenance stamp
				};
				var updated = await _graph.Users[_principal].Events[inst.Id].PatchAsync(patch);
				return new RemoteRef { Id = inst.Id!, Etag = ReadEtag(updated), ICalUid = updated?.ICalUId ?? string.Empty };
			}
			catch (ProviderException) { throw; }
			catch (Exception ex) { throw Translate(ex, "graph.instance-upsert"); }
		}

		public async Task CancelInstanceAsync(string mirrorMasterId, DateTime originalStartUtc)
		{
			try
			{
				var inst = await FindInstance(mirrorMasterId, originalStartUtc);
				if (inst?.Id == null) return;
				await _graph.Users[_principal].Events[inst.Id].DeleteAsync();
			}
			catch (ApiException ax) when (ax.ResponseStatusCode == 404) { }
			catch (Exception ex) { throw Translate(ex, "graph.instance-cancel"); }
		}

		private async Task<Event?> FindInstance(string masterId, DateTime originalStartUtc)
		{
			var resp = await _graph.Users[_principal].Events[masterId].Instances.GetAsync(rc =>
			{
				rc.QueryParameters.StartDateTime = originalStartUtc.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
				rc.QueryParameters.EndDateTime = originalStartUtc.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
				rc.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
			});
			return resp?.Value?.FirstOrDefault(o =>
			{
				var os = o.OriginalStart?.UtcDateTime ?? ParseGraphDate(o.Start);
				return Math.Abs((os - originalStartUtc).TotalMinutes) < 2;
			});
		}

		private List<SingleValueLegacyExtendedProperty> BuildStampProps(ProjectedUnit u, long pairId, long version)
		{
			var stamp = ProvenanceStamp.Build(u, pairId, version);
			var list = new List<SingleValueLegacyExtendedProperty>();
			foreach (var kv in stamp)
				list.Add(new SingleValueLegacyExtendedProperty { Id = $"String {{{_ns}}} Name {kv.Key}", Value = kv.Value });
			return list;
		}

		private static DateTimeTimeZone ToGraphDateTime(DateTime utc, bool allDay)
		{
			string s = allDay ? utc.ToString("yyyy-MM-ddT00:00:00", CultureInfo.InvariantCulture) : utc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
			return new DateTimeTimeZone { DateTime = s, TimeZone = "UTC" };
		}

		// Inside $expand=...($filter=...) a property is addressed directly (`id eq ...`).
		// The `ep/` alias belongs only to the list endpoint's `any(ep: ...)` form (see
		// FindByStampAsync); using it here makes Graph return 400.
		private string StampExpand() =>
			"singleValueExtendedProperties($filter=" +
			string.Join(" or ", StampKeys.All.Select(k => $"id eq 'String {{{_ns}}} Name {k}'")) + ")";

		private bool StampPairMatches(Event e, long pairId)
		{
			var map = ReadStampMap(e);
			return map.TryGetValue(StampKeys.Pair, out var p) && p == pairId.ToString();
		}

		private Dictionary<string, string> ReadStampMap(Event e)
		{
			var map = new Dictionary<string, string>();
			if (e.SingleValueExtendedProperties == null) return map;
			foreach (var svep in e.SingleValueExtendedProperties)
			{
				if (string.IsNullOrEmpty(svep.Id)) continue;
				int idx = svep.Id.IndexOf(" Name ", StringComparison.Ordinal);
				if (idx < 0) continue;
				string key = svep.Id.Substring(idx + 6);
				map[key] = svep.Value ?? string.Empty;
			}
			return map;
		}

		private RemoteEvent ToRemoteEvent(Event e)
		{
			bool removed = e.AdditionalData != null && e.AdditionalData.ContainsKey("@removed");
			var re = new RemoteEvent
			{
				Provider = Providers.M365,
				Id = e.Id ?? string.Empty,
				// Graph wraps an external invite's RFC UID in a GlobalObjectId; unwrap it
				// to match the clean UID Google exposes.
				ICalUid = GlobalObjectId.Normalize(e.ICalUId),
				Etag = ReadEtag(e),
				IsDeleted = removed || (e.IsCancelled ?? false),
				IsAllDay = e.IsAllDay ?? false,
				Subject = e.Subject ?? string.Empty,
				Body = e.Body?.Content,
				Location = e.Location?.DisplayName,
				ShowAs = MapShowAsFromGraph(e.ShowAs),
				IsPrivate = e.Sensitivity == Sensitivity.Private || e.Sensitivity == Sensitivity.Confidential,
				SeriesMasterId = e.SeriesMasterId,
				RecurrenceRule = null,   // set below for series masters
				TimeZoneId = "UTC"
			};
			re.StartUtc = ParseGraphDate(e.Start);
			re.EndUtc = ParseGraphDate(e.End);
			if (!string.IsNullOrEmpty(e.SeriesMasterId))
				re.OccurrenceOriginalStartUtc = e.OriginalStart?.UtcDateTime ?? re.StartUtc;
			if (e.Type == EventType.SeriesMaster || (e.Recurrence != null && string.IsNullOrEmpty(e.SeriesMasterId)))
			{
				re.IsSeriesMaster = true;
				re.RecurrenceRule = RecurrenceConverter.GraphToRRule(e.Recurrence);
			}
			re.AttendeeCount = e.Attendees?.Count ?? 0;
			if (e.Attendees != null)
				foreach (var a in e.Attendees)
					if (!string.IsNullOrWhiteSpace(a.EmailAddress?.Name)) re.AttendeeNames.Add(a.EmailAddress!.Name!);
			re.Stamp = Provenance.FromMap(ReadStampMap(e));
			return re;
		}

		private static DateTime ParseGraphDate(DateTimeTimeZone? dtz)
		{
			if (dtz?.DateTime == null) return DateTime.MinValue;
			if (DateTime.TryParse(dtz.DateTime, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
				return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
			return DateTime.MinValue;
		}

		private static string ReadEtag(Event? e)
		{
			if (e?.AdditionalData != null && e.AdditionalData.TryGetValue("@odata.etag", out var v))
				return v?.ToString() ?? string.Empty;
			return string.Empty;
		}

		private static FreeBusyStatus MapShowAsToGraph(string showAs) => showAs switch
		{
			"free" => FreeBusyStatus.Free,
			"tentative" => FreeBusyStatus.Tentative,
			"oof" => FreeBusyStatus.Oof,
			_ => FreeBusyStatus.Busy
		};

		private static string MapShowAsFromGraph(FreeBusyStatus? s) => s switch
		{
			FreeBusyStatus.Free => "free",
			FreeBusyStatus.Tentative => "tentative",
			FreeBusyStatus.Oof => "oof",
			_ => "busy"
		};

		private static string Escape(string s) => (s ?? string.Empty).Replace("'", "''");

		// Match-before-create lookups treat a failure as "not found", which is safe only for
		// a permanent refusal. A throttle, outage or dead grant would plan a duplicate
		// create, so those end the run and the next run looks again.
		private static void ThrowIfTransient(Exception ex, string op)
		{
			var pe = Translate(ex, op);
			if (pe.IsTransient) throw pe;
		}

		private static ProviderException Translate(Exception ex, string op)
		{
			NeedsReauthException.ThrowIfWrapped(ex);
			if (ex is ApiException ax)
			{
				int code = ax.ResponseStatusCode;
				bool transient = code == 429 || (code >= 500 && code < 600);
				int? retryAfter = null;
				if (ax.ResponseHeaders != null && ax.ResponseHeaders.TryGetValue("Retry-After", out var vals))
				{
					var first = vals?.FirstOrDefault();
					if (int.TryParse(first, out var ra)) retryAfter = ra;
				}
				return new ProviderException($"{op}: HTTP {code}: {ex.Message}", code.ToString(), transient, retryAfter, ex);
			}
			// Network/transport errors are transient.
			return new ProviderException($"{op}: {ex.Message}", "transport", true, null, ex);
		}
	}
}
