using System.Data;

namespace Core.MTCalSync
{
	// Non-sync operator commands (add-pair/list/status/history/dead-letters/…).
	// Logic lives in Core so the Worker CLI stays thin (house convention).
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
				// Operator pairs belong to the built-in default customer (free forever);
				// bootstrap-admin claims the userID.
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
				enabled = true
			};
			long pairId = pair.insert();
			Console.WriteLine($"Created sync pair {pairId}: {pair.name} [{pair.direction}, {pair.fidelityMode}, {pair.recurrenceMode}]");
			Console.WriteLine("Next: run `setup-check` to verify live calendar access, then `sync --pair " + pairId + " --dry-run`.");
			return pairId;
		}

		// ── shared-calendar mirroring (Portal "Shared calendars" page) ──────────
		// A shared-calendar mirror is a one-way google→m365 pair that writes into the
		// mailbox's PRIMARY calendar. Identified by (right connection = google/principal/
		// calId) + direction right_to_left. Many such pairs can safely share one M365
		// primary calendar — the engine's cross-pair foreign-mirror guard stops them from
		// cross-contaminating (and from leaking back through a bidirectional primary pair).

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

		// Check → start mirroring a shared Google calendar into the M365 primary calendar.
		// Idempotent: reuses/re-enables an existing mirror pair for the same calendar.
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
				customerID = 1,   // operator feature — default customer, free forever
				name = "Shared: " + label,
				leftConnectionID = leftId,
				rightConnectionID = rightId,
				direction = Directions.RightToLeft,          // google → m365 only
				fidelityMode = Fidelity.FullDetail,
				recurrenceMode = RecurrenceModes.Instance,   // keeps the cross-pair guard sufficient
				windowDays = Settings.WindowDays,
				lookbackDays = Settings.LookbackDays,
				copyAttendeesToBody = Settings.CopyAttendeesToBody,
				// First sync creates one event per in-window occurrence; lift the circuit
				// breaker well above the default 25 so a normal shared calendar won't trip it.
				maxWritesPerRun = Math.Max(Settings.MaxWritesPerRun, 500),
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

		// Uncheck / remove-pair. Deletes the events this pair mirrored onto the OTHER
		// side of each mapping (a bidirectional pair holds mirrors on both sides),
		// then the pair itself. Safe ordering: disable (stop the timer) → acquire the
		// per-pair lock (don't race a live run) → read mirror ids BEFORE deleting the
		// pair (the row delete cascades the mappings away) → stamp-verify + delete
		// each remote mirror (idempotent on 404) → delete the pair row → drop
		// now-unreferenced connections on both sides.
		//
		// STAMP GUARD: only events carrying OUR provenance stamp for this pair are
		// deleted. A mapping can link two REAL events (adopted external invites) —
		// its "mirror side" is a native event the user owns, and it must survive.
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
					// The mirror lives on the side OPPOSITE the unit's origin.
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
								// already gone remotely — nothing to delete
							}
							else if (ev.Stamp.Managed && ev.Stamp.PairId == pairId)
							{
								await prov.DeleteAsync(mirrorId, m.EtagForSide(mirrorSide));
								r.Deleted++;
							}
							else
							{
								keptReal++;   // adopted native event — never ours to delete
							}
						}
						catch (Exception ex) { r.Failed++; Common.writeToLog($"teardown pair={pairId} side={mirrorSide} mirror={mirrorId}:", ex); }
					}
					m.tombstone();
				}

				if (r.Failed > 0)
				{
					r.Message = $"Deleted {r.Deleted} mirrored event(s); {r.Failed} failed. Pair {pairId} kept (disabled) — retry removal.";
					return r;
				}

				new SyncPair().delete(pairId);   // cascade: mappings / state / runs / dead-letters / lock

				// Drop whichever connections no longer back any pair. (The historical
				// alsoDeleteGoogleConnection=false only preserves the google side for
				// callers that manage it themselves.)
				var pc = new ProviderConnection();
				if (pc.countReferencingPairs(pair.leftConnectionID) == 0)
					pc.delete(pair.leftConnectionID);
				if (alsoDeleteGoogleConnection && pc.countReferencingPairs(pair.rightConnectionID) == 0)
					pc.delete(pair.rightConnectionID);

				r.Removed = true;
				r.Message = $"Removed pair {pairId}; deleted {r.Deleted} mirrored event(s)" +
					(keptReal > 0 ? $"; left {keptReal} adopted native event(s) untouched." : ".");
				Common.audit($"teardown pair={pairId} deleted={r.Deleted} keptReal={keptReal}");
				return r;
			}
			finally { lockRow.release(); }
		}

		// Read-only forensic dump: every event currently in the rolling window on BOTH
		// sides of a pair (optionally filtered by subject substring), with provenance
		// stamp / attendee count / id / iCalUID. Diagnoses duplicates, orphans, and echo
		// tangles. Persists nothing (fresh SyncState, token not saved). M365 events are
		// re-fetched individually so the stamp is populated (calendarView/delta doesn't
		// expand singleValueExtendedProperties).
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
						Console.WriteLine($"      stamp: managed={(e.Stamp.Managed ? "YES" : "no")} origin={e.Stamp.OriginSystem} pair={e.Stamp.PairId} oid={e.Stamp.OriginId} originUid={e.Stamp.OriginICalUid}");
					}
				}
				catch (Exception ex) { Console.WriteLine("  ERROR: " + ex.Message); }
			}
		}

		// Operator cleanup: delete a MANAGED mirror event and tombstone its mapping. Guarded
		// — refuses unless the event carries OUR provenance stamp for this pair, so it can
		// never delete a real/native event. Used to clear stray duplicates.
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
			if (!ev.Stamp.Managed || ev.Stamp.PairId != pairId)
			{
				Console.WriteLine($"REFUSED: {provider} event \"{ev.Subject}\" is not a managed mirror of pair {pairId} " +
					$"(managed={ev.Stamp.Managed} pair={ev.Stamp.PairId}). Nothing deleted.");
				return;
			}
			await prov.DeleteAsync(eventId, ev.Etag);
			var m = new EventMapping().findBySideKey(pairId, provider, Common.Sha256Hex(eventId));
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

		public static void Pause(long pairId, bool paused)
		{
			bool ok = new SyncPair().setEnabled(pairId, !paused);
			Console.WriteLine(ok ? $"Pair {pairId} {(paused ? "paused" : "resumed")}." : $"Pair {pairId} not found.");
		}

		// Reset delta/sync tokens for a pair — the next run does a safe full reconcile
		// (match-before-create converges without duplicating).
		public static void Resync(long pairId)
		{
			new SyncState().resetTokens(pairId);
			var oDA = new DataAccess();
			oDA.updateData("update sync_state set windowEnd=NULL, lastFullResyncAt=NULL where pairID=@p",
				new Dictionary<string, object> { { "@p", pairId } });
			Console.WriteLine($"Tokens cleared for pair {pairId}. Next `sync` will full-resync and re-adopt via the match ladder.");
		}

		public static void TestEmail()
		{
			bool ok = Email.SendAlert("MT-CalSync test email",
				"This is a test alert from MT-CalSync. If you received it, failure notifications are configured correctly.");
			Console.WriteLine(ok ? "Test email sent to " + Settings.AlertTo : "Test email NOT sent — check SMTP settings (SmtpHost/AlertTo/SmtpFrom).");
		}

		// Encrypt a secret and store it in the DB settings table (DB-first resolution).
		public static void SetSecret(string name, string value)
		{
			if (string.IsNullOrWhiteSpace(name) || value == null) { Console.WriteLine("Usage: set-secret <name> <value>"); return; }
			string enc = Encryption.Encrypt(value);
			new Settings().saveByName(name, enc);
			Console.WriteLine($"Stored encrypted setting '{name}' ({enc.Length} chars). It overrides the settings.xml value.");
		}

		// Set the self-host portal's single operator password (PBKDF2 hash stored in the
		// settings table). The self-host portal reads Settings.SelfHostAdminPasswordHash.
		public static void SetAdminPassword(string password)
		{
			if (string.IsNullOrWhiteSpace(password)) { Console.WriteLine("Usage: set-admin-password --password <value>"); return; }
			string weak = PasswordHasher.CheckStrength(password);
			if (weak.Length > 0) { Console.WriteLine(weak); return; }
			new Settings().saveByName("SelfHostAdminPasswordHash", PasswordHasher.Hash(password));
			Console.WriteLine("Self-host operator password set.");
		}

		// Dismantle mirror-of-mirror chains: a fresh pair's first sync reads deltas,
		// and deltas carry no stamps — so a leftover foreign mirror can masquerade as
		// a real event and get mirrored BACK, doubling the true origin's copies on
		// both sides. A chain is proven by a targeted read: the mapping's ORIGIN-side
		// event carries a managed stamp (real events never do). Repair = delete the
		// bogus mirror we created, delete the fake origin if its stamp names a DEAD
		// pair, tombstone the mapping; the real event then re-mirrors cleanly.
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

					// 1) the mirror WE minted off the fake origin
					try
					{
						var mir = string.IsNullOrEmpty(m.MirrorEventId) ? null : await ProviderFor(m.MirrorSide).GetAsync(m.MirrorEventId);
						if (mir != null && mir.Stamp.Managed && mir.Stamp.PairId == pairId)
						{ await ProviderFor(m.MirrorSide).DeleteAsync(m.MirrorEventId, m.EtagForSide(m.MirrorSide)); mirrorsDeleted++; }
					}
					catch (Exception ex) { Common.writeToLog($"repair-chains pair={pairId} del mirror {m.MirrorEventId}:", ex); }

					// 2) the fake origin itself — only when its stamp names a pair that
					//    no longer exists (never touch another LIVE pair's mirror)
					if (originPairDead)
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

		// Delete stray mirrors left behind by a pair whose teardown could not finish
		// (the failure class behind the 2026-07-23 StampExpand bug): events in the
		// LIVE pair's two calendars stamped as belonging to a pair that no longer
		// exists. Triple-guarded — the stamp must be managed, must name the dead
		// pair, and the event must not sit inside any live mapping (adopted mirrors
		// keep their old pair's stamp until their origin next rewrites them).
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
						if (!ev.Stamp.Managed || ev.Stamp.PairId != deadPairId) continue;
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

		// Re-encrypt legacy-format secrets in the settings table under the current
		// DataEncryptionKey (v2 AES-GCM). Safe to re-run; skips values already v2.
		public static void MigrateSecrets()
		{
			if (string.IsNullOrWhiteSpace(Settings.DataEncryptionKey))
			{ Console.WriteLine("DataEncryptionKey is not set in settings.xml — aborting."); return; }

			string[] secretNames = { "GraphClientSecret", "SmtpPassword", "GoogleServiceAccountJson" };
			int migrated = 0, skipped = 0;
			foreach (var name in secretNames)
			{
				var row = new Settings().getByName(name);
				if (row.settingID == 0 || string.IsNullOrWhiteSpace(row.settingValue)) { skipped++; continue; }
				if (!Encryption.IsLegacy(row.settingValue)) { Console.WriteLine($"{name}: already v2."); skipped++; continue; }
				try
				{
					string plain = Encryption.Decrypt(row.settingValue);   // legacy path
					new Settings().saveByName(name, Encryption.Encrypt(plain));
					Console.WriteLine($"{name}: migrated to v2.");
					migrated++;
				}
				catch (Exception ex)
				{
					Console.WriteLine($"{name}: FAILED to migrate — {ex.Message}");
					Common.writeToLog($"ERROR MigrateSecrets({name}):", ex);
				}
			}
			Console.WriteLine($"Done. {migrated} migrated, {skipped} skipped.");
		}

		private static string Fmt(DateTime? dt) => dt.HasValue ? dt.Value.ToString("yyyy-MM-dd HH:mm 'UTC'") : "never";
	}
}
