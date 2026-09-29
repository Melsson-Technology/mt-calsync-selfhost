using System.Data;

namespace Core.MTCalSync
{
	// Operator commands other than sync. They live in Core so the Worker CLI stays thin.
	public static class CliCommands
	{
		public static long AddPair(string name, string m365Email, string googleEmail,
			string m365Cal, string googleCal, string direction, string fidelity, string recurrence)
		{
			if (string.IsNullOrWhiteSpace(m365Email) || string.IsNullOrWhiteSpace(googleEmail))
				throw new ArgumentException("add-pair requires --m365-email and --google-email.");

			var connL = new ProviderConnection
			{
				provider = Providers.M365, principalEmail = m365Email.Trim(),
				calendarId = string.IsNullOrWhiteSpace(m365Cal) ? "primary" : m365Cal.Trim(),
				displayName = "M365 " + m365Email.Trim()
			};
			long leftId = connL.ensure();

			var connR = new ProviderConnection
			{
				provider = Providers.Google, principalEmail = googleEmail.Trim(),
				calendarId = string.IsNullOrWhiteSpace(googleCal) ? "primary" : googleCal.Trim(),
				displayName = "Google " + googleEmail.Trim()
			};
			long rightId = connR.ensure();

			var pair = new SyncPair
			{
				// Operator pairs belong to the built-in owner, id 1.
				customerID = 1,
				name = string.IsNullOrWhiteSpace(name) ? $"{m365Email} <-> {googleEmail}" : name,
				leftConnectionID = leftId,
				rightConnectionID = rightId,
				direction = string.IsNullOrWhiteSpace(direction) ? Directions.Bidirectional : direction,
				fidelityMode = string.IsNullOrWhiteSpace(fidelity) ? Settings.FidelityMode : fidelity,
				recurrenceMode = string.IsNullOrWhiteSpace(recurrence) ? RecurrenceModes.Instance : recurrence,
				windowDays = Settings.WindowDays,
				lookbackDays = Settings.LookbackDays,
				copyAttendeesToBody = Settings.CopyAttendeesToBody,
				maxWritesPerRun = Settings.MaxWritesPerRun,
				fullResyncHour = Settings.FullResyncHour,
				// Created paused: the pair acts on mailboxes with app-only credentials, so
				// the operator dry-runs it before the timer picks it up.
				enabled = false
			};
			long pairId = pair.insert();
			Console.WriteLine($"Created sync pair {pairId} (paused): {pair.name} [{pair.direction}, {pair.fidelityMode}, {pair.recurrenceMode}]");
			Console.WriteLine($"Next: `setup-check` to test calendar access, `sync --pair {pairId} --dry-run` to preview it, " +
				$"then `resume --pair {pairId}` to start syncing.");
			return pairId;
		}

		// Shared-calendar mirroring
		// A shared-calendar mirror is a one-way Google-to-M365 pair that writes into the
		// mailbox's primary calendar, identified by its Google connection and right_to_left.
		// Many can share one M365 calendar: the cross-pair foreign-mirror guard keeps them
		// from copying each other's mirrors, or leaking back through a bidirectional pair.

		// The existing mirror pair for a given Google calendar, or null.
		public static SyncPair? FindSharedPair(string googlePrincipal, string googleCalId)
		{
			foreach (var pair in new SyncPair().listAll())
			{
				if (pair.direction != Directions.RightToLeft) continue;
				var r = new ProviderConnection().getById(pair.rightConnectionID);
				if (r.provider == Providers.Google &&
					string.Equals(r.principalEmail, googlePrincipal, StringComparison.OrdinalIgnoreCase) &&
					string.Equals(r.calendarId, googleCalId, StringComparison.Ordinal))
					return pair;
			}
			return null;
		}

		// Starts mirroring a shared Google calendar into the M365 primary calendar.
		// Idempotent: an existing mirror pair for the calendar is re-enabled and reused.
		public static long EnableSharedCalendarMirror(string m365Email, string googlePrincipal,
			string googleCalId, string googleCalSummary)
		{
			if (string.IsNullOrWhiteSpace(m365Email) || string.IsNullOrWhiteSpace(googlePrincipal) || string.IsNullOrWhiteSpace(googleCalId))
				throw new ArgumentException("EnableSharedCalendarMirror requires m365Email, googlePrincipal and googleCalId.");

			var existing = FindSharedPair(googlePrincipal, googleCalId);
			if (existing != null)
			{
				if (!existing.enabled) new SyncPair().setEnabled(existing.pairID, true);
				return existing.pairID;
			}

			string label = string.IsNullOrWhiteSpace(googleCalSummary) ? googleCalId.Trim() : googleCalSummary.Trim();
			var connL = new ProviderConnection
			{
				provider = Providers.M365, principalEmail = m365Email.Trim(),
				calendarId = "primary", displayName = "M365 " + m365Email.Trim()
			};
			long leftId = connL.ensure();

			var connR = new ProviderConnection
			{
				provider = Providers.Google, principalEmail = googlePrincipal.Trim(),
				calendarId = googleCalId.Trim(), displayName = "Shared: " + label
			};
			long rightId = connR.ensure();

			var pair = new SyncPair
			{
				customerID = 1,   // the built-in owner
				name = "Shared: " + label,
				leftConnectionID = leftId,
				rightConnectionID = rightId,
				direction = Directions.RightToLeft,          // google → m365 only
				fidelityMode = Fidelity.FullDetail,
				recurrenceMode = RecurrenceModes.Instance,   // keeps the cross-pair guard sufficient
				windowDays = Settings.WindowDays,
				lookbackDays = Settings.LookbackDays,
				copyAttendeesToBody = Settings.CopyAttendeesToBody,
				maxWritesPerRun = Settings.MaxWritesPerRun,   // the first sync has its own allowance
				fullResyncHour = Settings.FullResyncHour,
				enabled = true
			};
			long pairId = pair.insert();
			Common.audit($"shared-calendar enable pair={pairId} google={googlePrincipal}/{googleCalId} -> m365={m365Email}/primary");
			return pairId;
		}

		public class TeardownResult
		{
			public long PairId;
			public bool Removed;
			public bool Busy;
			public int Deleted;
			public int Failed;
			public string Message = string.Empty;
		}

		// Removes a pair and the mirrors it wrote. Order matters: disable it, take the pair
		// lock so no run is in flight, then read the mirror ids before deleting the pair,
		// because the row delete cascades the mappings away. Only events stamped for this
		// pair that pass MirrorGuard are deleted; a mapping can link two real copies of one
		// meeting, and the user's copy must survive.
		public static async Task<TeardownResult> TeardownPair(long pairId, bool alsoDeleteGoogleConnection = true)
		{
			var r = new TeardownResult { PairId = pairId };
			var pair = new SyncPair().getById(pairId);
			if (pair.pairID == 0) { r.Message = $"No sync pair {pairId}."; return r; }

			new SyncPair().setEnabled(pairId, false);

			var lockRow = new SyncLock(pairId);
			if (!lockRow.tryAcquire(600))
			{
				r.Busy = true;
				r.Message = $"Pair {pairId} is syncing right now; it has been paused — retry removal shortly.";
				return r;
			}
			try
			{
				var maps = new EventMapping().listByPair(pairId);
				var providers = new Dictionary<string, ICalendarProvider>();
				ICalendarProvider ProviderFor(string side) =>
					providers.TryGetValue(side, out var p) ? p
					: providers[side] = ProviderFactory.Create(
						new ProviderConnection().getById(side == Providers.M365 ? pair.leftConnectionID : pair.rightConnectionID),
						pair.SeriesMode);

				int keptReal = 0;
				foreach (var m in maps)
				{
					// The mirror lives on the side opposite the origin.
					string mirrorSide = Providers.Other(m.originProvider);
					string mirrorId = m.EventIdForSide(mirrorSide);
					if (!string.IsNullOrEmpty(mirrorId))
					{
						try
						{
							var prov = ProviderFor(mirrorSide);
							var ev = await prov.GetAsync(mirrorId);
							if (ev == null)
							{
								// already gone remotely
							}
							else if (ev.Stamp.PairId == pairId && MirrorGuard.RefusalReason(ev, m.ICalUidForSide(m.originProvider)) == null)
							{
								await prov.DeleteAsync(mirrorId, m.EtagForSide(mirrorSide));
								r.Deleted++;
							}
							else
							{
								keptReal++;   // a real event, not ours to delete
							}
						}
						catch (Exception ex)
						{
							// The mapping stays active, so the retry the message asks for finds this mirror again.
							r.Failed++; Common.writeToLog($"teardown pair={pairId} side={mirrorSide} mirror={mirrorId}:", ex);
							continue;
						}
					}
					m.tombstone();
				}

				if (r.Failed > 0)
				{
					r.Message = $"Deleted {r.Deleted} mirrored event(s); {r.Failed} failed. Pair {pairId} kept (disabled) — retry removal.";
					return r;
				}

				new SyncPair().delete(pairId);   // cascade: mappings / state / runs / dead-letters / lock

				// Drop connections that no longer back any pair. alsoDeleteGoogleConnection=false
				// keeps the Google side for callers that manage it themselves.
				var pc = new ProviderConnection();
				if (pc.countReferencingPairs(pair.leftConnectionID) == 0)
					pc.delete(pair.leftConnectionID);
				if (alsoDeleteGoogleConnection && pc.countReferencingPairs(pair.rightConnectionID) == 0)
					pc.delete(pair.rightConnectionID);

				r.Removed = true;
				r.Message = $"Removed pair {pairId}; deleted {r.Deleted} mirrored event(s)" +
					(keptReal > 0 ? $"; left {keptReal} adopted native event(s) untouched." : ".");
				Common.writeToLog($"MTCS teardown pair={pairId} deleted={r.Deleted} keptReal={keptReal}");   // the log only: the caller prints r.Message
				return r;
			}
			finally { lockRow.release(); }
		}

		// Read-only dump of every event in the window on both sides, with stamp, attendee
		// count, id and iCalUID, for diagnosing duplicates and orphans. Saves no token. M365
		// events are re-fetched one by one because delta doesn't expand
		// singleValueExtendedProperties, where the stamp lives.
		public static async Task Inspect(long pairId, string subjectFilter)
		{
			var pair = new SyncPair().getById(pairId);
			if (pair.pairID == 0) { Console.WriteLine($"No sync pair {pairId}."); return; }
			var connL = new ProviderConnection().getById(pair.leftConnectionID);
			var connR = new ProviderConnection().getById(pair.rightConnectionID);
			var window = RollingWindow.Around(DateTime.UtcNow, pair.lookbackDays, pair.windowDays);
			Console.WriteLine($"Inspect pair {pairId} ({pair.name}) — window {window.StartUtc:MM-dd}..{window.EndUtc:MM-dd}" +
				(string.IsNullOrWhiteSpace(subjectFilter) ? "" : $", subject~'{subjectFilter}'"));

			foreach (var conn in new[] { connL, connR })
			{
				Console.WriteLine($"\n===== {conn.provider}: {conn.principalEmail} cal={conn.calendarId} =====");
				try
				{
					var prov = ProviderFactory.Create(conn, pair.SeriesMode);
					var cs = await prov.GetChangesAsync(window, new SyncState { pairID = pairId, provider = conn.provider }, true);
					var items = cs.Items
						.Where(e => string.IsNullOrWhiteSpace(subjectFilter) || (e.Subject ?? "").Contains(subjectFilter, StringComparison.OrdinalIgnoreCase))
						.OrderBy(e => e.StartUtc).ToList();
					Console.WriteLine($"{items.Count} matching event(s):");
					foreach (var it in items)
					{
						var e = it;
						if (conn.provider == Providers.M365 && !string.IsNullOrEmpty(it.Id))
						{
							try { var full = await prov.GetAsync(it.Id); if (full != null) e = full; } catch { }
						}
						Console.WriteLine($"  \"{e.Subject}\"  [{e.StartUtc:yyyy-MM-dd HH:mm}-{e.EndUtc:HH:mm}Z]{(e.IsDeleted ? " (DELETED)" : "")}");
						Console.WriteLine($"      id={e.Id}");
						Console.WriteLine($"      uid={e.ICalUid}");
						Console.WriteLine($"      attendees={e.AttendeeNames.Count}  allDay={e.IsAllDay}  showAs={e.ShowAs}  seriesMaster={e.IsSeriesMaster}");
						// `it` is what the engine saw in the delta read; `e` is the full event. A
						// difference means the delta is lossy, which breaks projection and adoption.
						if (conn.provider == Providers.M365 && (it.Subject != e.Subject || it.ICalUid != e.ICalUid || it.IsAllDay != e.IsAllDay || it.IsRecurringInstance != e.IsRecurringInstance))
							Console.WriteLine($"      delta-read: subj=\"{it.Subject}\" uid=\"{it.ICalUid}\" allDay={it.IsAllDay} type={(it.IsSeriesMaster ? "master" : it.IsRecurringInstance ? "occurrence" : "single")} seriesMasterId={it.SeriesMasterId}");
						Console.WriteLine($"      stamp: managed={(e.Stamp.Managed ? "YES" : "no")} origin={e.Stamp.OriginSystem} pair={e.Stamp.PairId} oid={e.Stamp.OriginId} originUid={e.Stamp.OriginICalUid}");
					}
				}
				catch (Exception ex) { Console.WriteLine("  ERROR: " + ex.Message); }
			}
		}

		// Read-only: reports which mirrors of active mappings the sync would refuse to update or
		// delete, and why. Useful before and after a change to the write paths.
		public static async Task GuardAudit(long pairId)
		{
			var pair = new SyncPair().getById(pairId);
			if (pair.pairID == 0) { Console.WriteLine($"No sync pair {pairId}."); return; }
			var providers = new Dictionary<string, ICalendarProvider>();
			ICalendarProvider ProviderFor(string side) =>
				providers.TryGetValue(side, out var p) ? p
				: providers[side] = ProviderFactory.Create(
					new ProviderConnection().getById(side == Providers.M365 ? pair.leftConnectionID : pair.rightConnectionID),
					pair.SeriesMode);

			int writable = 0, gone = 0, failed = 0;
			var refused = new Dictionary<string, int>();
			foreach (var m in new EventMapping().listByPair(pairId))
			{
				string mirrorId = m.MirrorEventId;
				if (string.IsNullOrEmpty(mirrorId)) { gone++; continue; }
				try
				{
					var ev = await ProviderFor(m.MirrorSide).GetAsync(mirrorId);
					if (ev == null || ev.IsDeleted) { gone++; continue; }
					string? why = MirrorGuard.RefusalReason(ev, m.ICalUidForSide(m.originProvider));
					if (why == null) { writable++; continue; }
					refused[why] = refused.GetValueOrDefault(why) + 1;
					Console.WriteLine($"  refused  mapping {m.mappingID}  {m.MirrorSide} {Trunc(mirrorId)}  {why}  (stamp pair {ev.Stamp.PairId}, attendees {ev.AttendeeCount})");
				}
				catch (Exception ex) { failed++; Console.WriteLine($"  error    mapping {m.mappingID}: {ex.Message}"); }
			}
			Console.WriteLine($"Pair {pairId}: {writable} mirror(s) writable, {refused.Values.Sum()} refused, {gone} gone, {failed} unreadable.");
			foreach (var kv in refused) Console.WriteLine($"  {kv.Value} × {kv.Key}");
		}

		// Deletes one mirror event and tombstones its mapping. Refuses anything that is not
		// this pair's own mirror (see MirrorGuard).
		public static async Task PurgeMirror(long pairId, string provider, string eventId)
		{
			var pair = new SyncPair().getById(pairId);
			if (pair.pairID == 0) { Console.WriteLine($"No sync pair {pairId}."); return; }
			if (provider != Providers.M365 && provider != Providers.Google) { Console.WriteLine("--provider must be 'm365' or 'google'."); return; }
			if (string.IsNullOrWhiteSpace(eventId)) { Console.WriteLine("--id required."); return; }

			var conn = new ProviderConnection().getById(provider == Providers.M365 ? pair.leftConnectionID : pair.rightConnectionID);
			var prov = ProviderFactory.Create(conn, pair.SeriesMode);
			var ev = await prov.GetAsync(eventId);
			if (ev == null) { Console.WriteLine($"Event {eventId} not found on {provider} (already gone?)."); return; }
			var m = new EventMapping().findBySideKey(pairId, provider, Common.Sha256Hex(eventId));
			string? why = ev.Stamp.PairId != pairId ? $"not stamped for pair {pairId}"
				: MirrorGuard.RefusalReason(ev, m?.ICalUidForSide(m.originProvider));
			if (why != null)
			{
				Console.WriteLine($"REFUSED: {provider} event \"{ev.Subject}\" is not a mirror of pair {pairId} ({why}). Nothing deleted.");
				return;
			}
			await prov.DeleteAsync(eventId, ev.Etag);
			if (m != null) m.tombstone();
			Console.WriteLine($"Deleted {provider} mirror {eventId} (\"{ev.Subject}\"); tombstoned mapping {(m != null ? m.mappingID.ToString() : "none")}.");
			Common.audit($"purge-mirror pair={pairId} side={provider} id={eventId} mapping={(m != null ? m.mappingID.ToString() : "none")} result=ok");
		}

		public static void ListPairs()
		{
			var pairs = new SyncPair().listAll();
			if (pairs.Count == 0) { Console.WriteLine("No sync pairs. Create one with `add-pair`."); return; }
			foreach (var p in pairs)
			{
				var l = new ProviderConnection().getById(p.leftConnectionID);
				var r = new ProviderConnection().getById(p.rightConnectionID);
				Console.WriteLine($"[{p.pairID}] {p.name}  {(p.enabled ? "" : "(PAUSED) ")}{p.direction} / {p.fidelityMode} / {p.recurrenceMode}");
				Console.WriteLine($"      M365   {l.principalEmail} cal={l.calendarId}");
				Console.WriteLine($"      Google {r.principalEmail} cal={r.calendarId}");
				Console.WriteLine($"      window=-{p.lookbackDays}d..+{p.windowDays}d  maxWrites/run={p.maxWritesPerRun}  copyAttendees={p.copyAttendeesToBody}");
			}
		}

		public static void Status()
		{
			var pairs = new SyncPair().listAll();
			if (pairs.Count == 0) { Console.WriteLine("No sync pairs."); return; }
			var ss = new SyncState();
			var dl = new DeadLetter();
			var em = new EventMapping();
			foreach (var p in pairs)
			{
				var sL = ss.getByPairProvider(p.pairID, Providers.M365);
				var sR = ss.getByPairProvider(p.pairID, Providers.Google);
				Console.WriteLine($"[{p.pairID}] {p.name}  {(p.enabled ? "enabled" : "PAUSED")}");
				Console.WriteLine($"      m365   token={(sL.HasToken ? "yes" : "no")}  lastSuccess={Fmt(sL.lastSuccessfulRunAt)}  fails={sL.consecutiveFailures}");
				Console.WriteLine($"      google token={(sR.HasToken ? "yes" : "no")}  lastSuccess={Fmt(sR.lastSuccessfulRunAt)}  fails={sR.consecutiveFailures}");
				Console.WriteLine($"      mappings(active)={em.countActive(p.pairID)}  openDeadLetters={dl.countOpen(p.pairID)}");
			}
		}

		public static void History(long pairId, int limit)
		{
			var runs = new SyncRun().recent(pairId, limit);
			if (runs.Count == 0) { Console.WriteLine($"No runs for pair {pairId}."); return; }
			Console.WriteLine($"Recent runs for pair {pairId} (newest first):");
			foreach (var r in runs)
				Console.WriteLine($"  run {r.runID,-6} {r.status,-24} created={r.createdCount} updated={r.updatedCount} deleted={r.deletedCount} echo={r.echoSkippedCount} conflicts={r.conflictCount} dead={r.deadLetteredCount}");
		}

		public static void DeadLetters(long pairId, long resolveId, bool resolveAll)
		{
			var dl = new DeadLetter();
			if (resolveId > 0) { dl.resolve(pairId, resolveId); Console.WriteLine($"Resolved dead-letter {resolveId}."); return; }
			if (resolveAll) { dl.resolve(pairId); Console.WriteLine($"Resolved all open dead-letters for pair {pairId}."); return; }
			var items = dl.listOpen(pairId);
			if (items.Count == 0) { Console.WriteLine($"No open dead-letters for pair {pairId}."); return; }
			Console.WriteLine($"Open dead-letters for pair {pairId}:");
			foreach (var d in items)
				Console.WriteLine($"  [{d.deadLetterID}] {d.operation} {d.sourceProvider}:{d.sourceEventId} attempts={d.attemptCount} code={d.errorCode} — {d.errorText}");
			Console.WriteLine("Resolve with: dead-letters --pair " + pairId + " --resolve <id>   (or --resolve-all)");
		}

		// Returns false for a pair that doesn't exist, so the CLI can exit non-zero.
		public static bool Pause(long pairId, bool paused)
		{
			bool ok = new SyncPair().getById(pairId).pairID != 0 && new SyncPair().setEnabled(pairId, !paused);
			Console.WriteLine(ok ? $"Pair {pairId} {(paused ? "paused" : "resumed")}." : $"Pair {pairId} not found.");
			return ok;
		}

		// The circuit breaker's limit: a run that would make more changes applies none.
		public static bool SetMaxWrites(long pairId, long max)
		{
			if (max < 1) { Console.WriteLine("Give the limit as a positive number, for example --max 25."); return false; }
			bool ok = new SyncPair().getById(pairId).pairID != 0 && new SyncPair().setMaxWritesPerRun(pairId, (int)max);
			Console.WriteLine(ok ? $"Pair {pairId} may now make up to {max} changes per run." : $"Pair {pairId} not found.");
			return ok;
		}

		// Clears the pair's tokens so the next run does a full reconcile; match-before-create
		// keeps that from duplicating events.
		public static void Resync(long pairId)
		{
			new SyncState().resetTokens(pairId);
			var oDA = new DataAccess();
			oDA.updateData("update sync_state set windowEnd=NULL, lastFullResyncAt=NULL where pairID=@p",
				new Dictionary<string, object> { { "@p", pairId } });
			Console.WriteLine($"Tokens cleared for pair {pairId}. Next `sync` will full-resync and re-adopt via the match ladder.");
		}

		public static bool TestEmail()
		{
			bool ok = Email.SendAlert("MT-CalSync test email",
				"This is a test alert from MT-CalSync. If you received it, failure notifications are configured correctly.");
			Console.WriteLine(ok ? "Test email sent to " + Settings.AlertTo : "Test email NOT sent. Check the SMTP settings (SmtpHost/AlertTo/SmtpFrom) and the worker log.");
			return ok;
		}

		// Stores an encrypted secret in the settings table, which overrides settings.xml.
		// Returns false, having said why, if nothing was stored.
		public static bool SetSecret(string name, string value)
		{
			if (string.IsNullOrWhiteSpace(name) || value == null) { Console.WriteLine("Usage: set-secret <name> <value>"); return false; }
			string enc = Encryption.Encrypt(value);
			if (!TrySave(name, enc)) return false;
			Console.WriteLine($"Stored encrypted setting '{name}' ({enc.Length} chars). It overrides the settings.xml value.");
			return true;
		}

		// Sets the portal's operator password, stored as a PBKDF2 hash in
		// Settings.SelfHostAdminPasswordHash. Returns false, having said why, if not stored.
		public static bool SetAdminPassword(string password)
		{
			if (string.IsNullOrWhiteSpace(password)) { Console.WriteLine("No password given, so nothing was changed. Run set-admin-password and enter it at the prompt."); return false; }
			string weak = PasswordHasher.CheckStrength(password);
			if (weak.Length > 0) { Console.WriteLine(weak); return false; }
			if (!TrySave("SelfHostAdminPasswordHash", PasswordHasher.Hash(password))) return false;
			Console.WriteLine("Self-host operator password set.");
			return true;
		}

		// saveByName reports failure through errorMessage rather than by throwing, so every
		// save must check it before reporting success.
		private static bool TrySave(string name, string value)
		{
			var settings = new Settings();
			settings.saveByName(name, value);
			if (string.IsNullOrEmpty(settings.errorMessage)) return true;
			Console.WriteLine($"Could not save '{name}' to the database: {settings.errorMessage}");
			return false;
		}

		// Dismantles mirror-of-mirror chains. Deltas carry no stamps, so a leftover foreign
		// mirror can look like a real event and be mirrored back. A chain shows as a mapping
		// whose origin-side event has a managed stamp, which real events never have. Repair
		// deletes our mirror, deletes the fake origin only if its pair is gone, and tombstones
		// the mapping so the real event re-mirrors.
		public static async Task RepairChains(long pairId, bool apply)
		{
			var pair = new SyncPair().getById(pairId);
			if (pair.pairID == 0) { Console.WriteLine($"No sync pair {pairId}."); return; }

			var lockRow = new SyncLock(pairId);
			if (!lockRow.tryAcquire(600))
			{ Console.WriteLine($"Pair {pairId} is syncing right now — retry shortly."); return; }
			try
			{
				var providers = new Dictionary<string, ICalendarProvider>();
				ICalendarProvider ProviderFor(string side) =>
					providers.TryGetValue(side, out var p) ? p
					: providers[side] = ProviderFactory.Create(
						new ProviderConnection().getById(side == Providers.M365 ? pair.leftConnectionID : pair.rightConnectionID),
						pair.SeriesMode);

				int chains = 0, mirrorsDeleted = 0, fakeOriginsDeleted = 0;
				foreach (var m in new EventMapping().listByPair(pairId))
				{
					string originSide = m.originProvider;
					string originId = m.EventIdForSide(originSide);
					if (string.IsNullOrEmpty(originId)) continue;

					RemoteEvent? origin = null;
					try { origin = await ProviderFor(originSide).GetAsync(originId); }
					catch (Exception ex) { Common.writeToLog($"repair-chains pair={pairId} read {originSide}:{originId}:", ex); continue; }
					if (origin == null || !origin.Stamp.Managed) continue;   // healthy mapping

					chains++;
					bool originPairDead = new SyncPair().getById(origin.Stamp.PairId).pairID == 0;
					Console.WriteLine($"chain: mapping {m.mappingID} — '{originSide}' origin {Trunc(originId)} is a MIRROR " +
						$"(stamp pair {origin.Stamp.PairId}{(originPairDead ? ", dead" : ", LIVE!")}) → its '{m.MirrorSide}' copy {Trunc(m.MirrorEventId)} is bogus");
					if (!apply) continue;

					// 1) the mirror we made of the fake origin
					try
					{
						var mir = string.IsNullOrEmpty(m.MirrorEventId) ? null : await ProviderFor(m.MirrorSide).GetAsync(m.MirrorEventId);
						if (mir != null && mir.Stamp.PairId == pairId && MirrorGuard.RefusalReason(mir, m.ICalUidForSide(originSide)) == null)
						{ await ProviderFor(m.MirrorSide).DeleteAsync(m.MirrorEventId, m.EtagForSide(m.MirrorSide)); mirrorsDeleted++; }
					}
					catch (Exception ex) { Common.writeToLog($"repair-chains pair={pairId} del mirror {m.MirrorEventId}:", ex); }

					// 2) the fake origin, only when its pair no longer exists; a live
					//    pair's mirror is never ours to delete
					if (originPairDead && MirrorGuard.RefusalReason(origin) == null)
					{
						try { await ProviderFor(originSide).DeleteAsync(originId, m.EtagForSide(originSide)); fakeOriginsDeleted++; }
						catch (Exception ex) { Common.writeToLog($"repair-chains pair={pairId} del fake origin {originId}:", ex); }
					}

					m.tombstone();
					Common.writeToLog($"repair-chains pair={pairId} dismantled mapping {m.mappingID} (stamp pair {origin.Stamp.PairId})");
				}
				Console.WriteLine(apply
					? $"Repair complete: {chains} chain(s) dismantled; {mirrorsDeleted} bogus mirror(s) + {fakeOriginsDeleted} dead-pair fake origin(s) deleted. Next run re-mirrors the real events."
					: $"Report only: {chains} chain(s) found. Re-run with --apply to dismantle.");
			}
			finally { lockRow.release(); }
		}

		private static string Trunc(string s) => s.Length <= 20 ? s : s[..20] + "…";

		// Deletes mirrors in the live pair's calendars that are stamped for a pair that no
		// longer exists, such as after an interrupted teardown. The stamp must be managed and
		// name the dead pair, and the event must not be in any live mapping: adopted mirrors
		// keep their old pair's stamp until their origin next rewrites them.
		public static async Task SweepStrays(long livePairId, long deadPairId)
		{
			var live = new SyncPair().getById(livePairId);
			if (live.pairID == 0) { Console.WriteLine($"No sync pair {livePairId}."); return; }
			if (new SyncPair().getById(deadPairId).pairID != 0)
			{ Console.WriteLine($"Pair {deadPairId} still exists — refusing to sweep a live pair's mirrors."); return; }

			var lockRow = new SyncLock(livePairId);
			if (!lockRow.tryAcquire(600))
			{ Console.WriteLine($"Pair {livePairId} is syncing right now — retry shortly."); return; }
			try
			{
				var mapModel = new EventMapping();
				int deleted = 0, keptMapped = 0, scanned = 0;
				foreach (string side in new[] { Providers.M365, Providers.Google })
				{
					var conn = new ProviderConnection().getById(side == Providers.M365 ? live.leftConnectionID : live.rightConnectionID);
					var prov = ProviderFactory.Create(conn, live.SeriesMode);
					var strays = await prov.ListByStampPairAsync(deadPairId, 250);
					foreach (var ev in strays)
					{
						scanned++;
						if (ev.Stamp.PairId != deadPairId || MirrorGuard.RefusalReason(ev) != null) continue;
						if (mapModel.existsActiveForSide(side, ev.Id)) { keptMapped++; continue; }
						try
						{
							await prov.DeleteAsync(ev.Id, ev.Etag);
							deleted++;
							Common.writeToLog($"sweep-strays live={livePairId} dead={deadPairId} side={side} deleted {ev.Id}");
						}
						catch (Exception ex) { Common.writeToLog($"sweep-strays side={side} event={ev.Id}:", ex); }
					}
				}
				Console.WriteLine($"Sweep complete: scanned {scanned} stamped stray(s) for dead pair {deadPairId}; deleted {deleted}; kept {keptMapped} (inside live mappings).");
			}
			finally { lockRow.release(); }
		}

		// Checks that every stored secret is in the v2 (AES-GCM) format. Pre-v2 ciphertext
		// can't be decrypted, so a legacy value is reported for re-entry. A secret that can't
		// be read counts as a failure.
		public static bool MigrateSecrets()
		{
			if (string.IsNullOrWhiteSpace(Settings.DataEncryptionKey))
			{ Console.WriteLine("DataEncryptionKey is not set in settings.xml, so nothing was checked."); return false; }

			string[] secretNames = { "GraphClientSecret", "SmtpPassword", "GoogleServiceAccountJson", "MsOAuthClientSecret", "GoogleOAuthClientSecret" };
			int current = 0, notSet = 0, failed = 0;
			foreach (var name in secretNames)
			{
				var reader = new Settings();
				var row = reader.getByName(name);
				if (!string.IsNullOrEmpty(reader.errorMessage)) { Console.WriteLine($"{name}: could not be read: {reader.errorMessage}"); failed++; continue; }
				if (row.settingID == 0 || string.IsNullOrWhiteSpace(row.settingValue)) { notSet++; continue; }
				if (!Encryption.IsLegacy(row.settingValue)) { current++; continue; }
				Console.WriteLine($"{name}: stored in the pre-v2 format, which this version can't decrypt. Re-enter it in Settings or with set-secret.");
				failed++;
			}
			Console.WriteLine($"Done. {current} current (v2), {notSet} not set, {failed} to fix.");
			return failed == 0;
		}

		private static string Fmt(DateTime? dt) => dt.HasValue ? dt.Value.ToString("yyyy-MM-dd HH:mm 'UTC'") : "never";
	}
}
