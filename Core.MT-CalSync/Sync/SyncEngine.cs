namespace Core.MTCalSync
{
	// The invariant core. One oneshot run over one sync_pair:
	//   lease → pull deltas per source side → classify (echo/native/delete) →
	//   plan ops → circuit-breaker → apply (retry/dead-letter) → persist tokens.
	// Governing invariant: a mirror is a read-only reflection of a single origin event,
	// and the origin side always wins.
	public class SyncEngine
	{
		private enum OpKind { Create, Update, Delete, InstanceUpsert, InstanceCancel }

		private class SyncOp
		{
			public OpKind Kind;
			public string SrcSide = string.Empty;   // origin provider
			public string DstSide = string.Empty;   // mirror provider (where the write lands)
			public string SourceId = string.Empty;  // origin event id (for dead-letter)
			public ProjectedUnit? Unit;
			public RemoteEvent? Source;
			public EventMapping? Mapping;
			// Series-mode exception overrides only:
			public string OriginMasterId = string.Empty;   // origin series master id
			public DateTime OrigStartUtc;                   // the occurrence's original start

			public bool IsInstanceOp => Kind == OpKind.InstanceUpsert || Kind == OpKind.InstanceCancel;
		}

		private readonly SyncPair _pair;
		private readonly bool _dryRun;
		private readonly bool _force;        // bypass the circuit breaker
		private readonly bool _fullResync;   // force a full reconcile (ignore stored tokens)
		private IEventProjection _projection = new FullDetailProjection();
		private RollingWindow _window = new();        // what gets mirrored
		private RollingWindow _tokenRange = new();    // what a full list reads, and so what its token covers

		// How far past the window's end a full list reaches. The window slides with the clock and
		// a token only reports changes inside the range it was minted over, so the margin keeps a
		// token valid between full resyncs. Two days outlives one missed daily resync.
		private static readonly TimeSpan TokenSlack = TimeSpan.FromDays(2);
		private readonly Dictionary<string, ICalendarProvider> _providers = new();
		private readonly EventMapping _map = new();
		private readonly DeadLetter _dl = new();
		private readonly List<EventMapping> _deferredMappings = new();

		// How many changes a pair's first sync may make. It mirrors every event already in the
		// window, so the ordinary limit would stop it.
		public const int FirstSyncAllowance = 500;

		// Classification also runs in a dry run and before the circuit breaker, so it never
		// saves a mapping itself: it queues it here, and Run saves the queue once the breaker
		// has passed.
		private void Defer(EventMapping m) { if (!_deferredMappings.Contains(m)) _deferredMappings.Add(m); }

		// What the circuit breaker counts as one change: the origin event, or for an occurrence
		// the series it belongs to.
		private static string ChangeKey(SyncOp o)
		{
			if (o.IsInstanceOp) return o.SrcSide + "|series|" + o.OriginMasterId;
			string series = o.Unit?.OriginSeriesKey ?? o.Mapping?.originSeriesKey ?? string.Empty;
			return string.IsNullOrEmpty(series) ? o.SrcSide + "|" + o.SourceId : o.SrcSide + "|series|" + series;
		}

		public SyncEngine(SyncPair pair, bool dryRun = false, bool force = false, bool fullResync = false)
		{
			_pair = pair; _dryRun = dryRun; _force = force; _fullResync = fullResync;
		}

		// Run one pair by id. Returns the completed SyncRun (null if pair missing).
		public static async Task<SyncRun?> RunPairById(long pairId, string trigger, bool dryRun, bool force, bool fullResync)
		{
			var pair = new SyncPair().getById(pairId);
			if (pair.pairID == 0) { Console.Error.WriteLine($"No sync pair {pairId}."); return null; }
			return await new SyncEngine(pair, dryRun, force, fullResync).Run(trigger);
		}

		// Run all enabled pairs (what the timer invokes). Returns overall exit code.
		public static async Task<int> RunAllEnabled(string trigger, bool dryRun, bool force, bool fullResync)
		{
			var pairs = new SyncPair().listAll(enabledOnly: true);
			if (pairs.Count == 0) { Console.WriteLine("No enabled sync pairs."); return 0; }
			int worst = 0;
			foreach (var pair in pairs)
			{
				var run = await new SyncEngine(pair, dryRun, force, fullResync).Run(trigger);
				if (run != null && (run.status == "failed" || run.status == "aborted_circuit_breaker")) worst = 2;
			}
			return worst;
		}

		public async Task<SyncRun?> Run(string trigger)
		{
			_projection = ProjectionFactory.For(_pair);
			var now = DateTime.UtcNow;
			_window = RollingWindow.Around(now, _pair.lookbackDays, _pair.windowDays);
			_tokenRange = _window.ExtendedBy(TokenSlack);

			var lockRow = new SyncLock(_pair.pairID);
			if (!_dryRun && !lockRow.tryAcquire(600))
			{
				Common.audit($"pair={_pair.pairID} op=lock result=skipped_locked");
				var s = SyncRun.start(_pair.pairID, trigger, "incremental"); s.finish("skipped_locked"); return s;
			}

			// A dry run gets an unsaved run, so a preview never shows as the pair's latest success.
			var run = _dryRun
				? new SyncRun { pairID = _pair.pairID, triggerType = "dry-run", syncType = "incremental", status = "running" }
				: SyncRun.start(_pair.pairID, trigger, "incremental");
			var pendingTokens = new Dictionary<string, (string? token, bool wasFull, RollingWindow range)>();
			try
			{
				BuildProviders();

				// Plan: pull and classify each source side.
				var ops = new List<SyncOp>();
				foreach (var side in Directions.SourceSides(_pair.direction))
				{
					var state = new SyncState().getByPairProvider(_pair.pairID, side);
					bool forceFull = _fullResync || _force || DueForFullResync(state, now) || !TokenCovers(state, _window);

					ChangeSet changes;
					try { changes = await PullChanges(side, state, forceFull); }
					catch (ProviderException pe)
					{
						if (!_dryRun) state.recordFailure(pe.Message);   // feeds the scheduler's backoff
						throw;   // abort the run; tokens for this side are not advanced
					}
					// Labelled by what was read, not by what was asked for: a provider falls back
					// to a full list by itself when its token has expired.
					if (changes.WasFullSync) run.syncType = "full_resync";

					if (side == Providers.M365) run.leftChanges += changes.Items.Count; else run.rightChanges += changes.Items.Count;
					// An incremental token covers the range its full list was minted over, so that
					// range is carried forward unchanged (TokenCovers guarantees it's on record).
					var range = changes.WasFullSync ? _tokenRange
						: new RollingWindow { StartUtc = state.windowStart!.Value, EndUtc = state.windowEnd!.Value };
					pendingTokens[side] = (changes.NewToken, changes.WasFullSync, range);

					foreach (var c in changes.Items)
					{
						var op = await Classify(side, c, run);
						if (op != null) ops.Add(op);
					}
				}

				// Masters and singles apply before occurrence overrides, which need their mirror
				// master to exist. The sort is stable.
				ops = ops.OrderBy(o => o.IsInstanceOp ? 1 : 0).ToList();

				// The breaker counts every planned write, updates included, and counts a recurring
				// series once (instance mode plans a create per occurrence). A first sync may make
				// up to FirstSyncAllowance; after that the pair's own limit applies.
				int planned = ops.Select(ChangeKey).Distinct().Count();
				bool firstSync = _map.countActive(_pair.pairID) == 0;
				int limit = firstSync ? Math.Max(_pair.maxWritesPerRun, FirstSyncAllowance) : _pair.maxWritesPerRun;
				bool overBreaker = planned > limit && !_force;

				// Dry run: print the plan and change nothing. It comes before the breaker because a
				// preview is how an operator learns whether a pair would trip it, and a preview
				// never alerts anyone.
				if (_dryRun)
				{
					Console.WriteLine($"DRY RUN — pair {_pair.pairID} ({_pair.name}): {ops.Count} op(s)");
					foreach (var o in ops)
					{
						string label = o.Kind == OpKind.InstanceCancel ? "(cancel occurrence)" : (o.Unit?.Subject ?? "(delete)");
						string when = o.IsInstanceOp ? o.OrigStartUtc.ToString("u") : (o.Unit?.StartUtc.ToString("u") ?? "");
						Console.WriteLine($"  {o.Kind,-14} {o.SrcSide}->{o.DstSide}  {label}  start={when}");
					}
					if (overBreaker)
						Console.WriteLine($"Note: {planned} planned changes exceed the limit of {limit}, so a live run " +
							$"would stop at the circuit breaker and apply nothing. If this is expected, run `sync --pair {_pair.pairID} --force` once.");
					run.finish("success");
					return run;
				}

				// Circuit breaker
				if (overBreaker)
				{
					string msg = $"Circuit breaker: {planned} planned changes exceed the limit of {limit}. Nothing was applied, " +
								 "and the next run will stop here too while the changes are still pending. " +
								 $"Preview them with `sync --pair {_pair.pairID} --dry-run`; if they are expected, apply them once with " +
								 $"`sync --pair {_pair.pairID} --force`.";
					Common.audit($"pair={_pair.pairID} op=circuit-breaker planned={planned} limit={limit} result=aborted");
					run.errorText = msg; run.finish("aborted_circuit_breaker");
					if (new SyncPair().shouldAlert(_pair.pairID))
						Email.SendAlert($"MT-CalSync circuit breaker (pair {_pair.pairID})", msg + "\n\n" + run.Summary());
					return run;
				}

				// Mapping changes that classification decided on (adoptions and re-keys) are
				// saved only now, so a run the breaker stops leaves the database as it found it.
				foreach (var m in _deferredMappings) m.save();   // a dry run returned above, so it saves none

				// Apply
				foreach (var op in ops) await ApplyWithRetry(op, run);

				// Persist tokens only after the apply pass.
				foreach (var side in Directions.SourceSides(_pair.direction))
				{
					if (!pendingTokens.TryGetValue(side, out var pt)) continue;
					var state = new SyncState { pairID = _pair.pairID, provider = side };
					state.saveAfterRun(pt.token, pt.range, pt.wasFull);
				}

				string final = run.deadLetteredCount > 0 ? "partial" : "success";
				run.finish(final);
				Common.audit($"pair={_pair.pairID} run={run.runID} {run.Summary()}");
				if ((final == "partial" || _dl.countOpen(_pair.pairID) > 0) && new SyncPair().shouldAlert(_pair.pairID))
					Email.SendAlert($"MT-CalSync partial run (pair {_pair.pairID})", run.Summary());
				return run;
			}
			catch (NeedsReauthException nre)
			{
				// A revoked or expired OAuth grant is not a sync fault: record skipped_auth (no
				// dead-letter, no operator alert) and ask the owner to reconnect, once per 24h.
				run.errorText = nre.Message;
				run.finish("skipped_auth");
				Common.audit($"pair={_pair.pairID} op=auth result=skipped_auth account={nre.OAuthAccountId}");
				// A dry run reports to the terminal rather than emailing the owner about a preview.
				if (_dryRun) Console.WriteLine($"DRY RUN stopped: {nre.Message}");
				else NotifyOwnerReauth(nre);
				return run;
			}
			catch (Exception ex)
			{
				run.errorText = ex.Message;
				run.finish("failed");
				Common.writeToLog($"FATAL sync pair {_pair.pairID}:", ex);
				// A failed dry run prints its reason, which otherwise reaches only the log.
				if (_dryRun) Console.WriteLine($"DRY RUN failed: {ex.Message}");
				// Transient provider conditions (429, 5xx, transport retry exhaustion) are
				// account-wide and self-healing: back the account off and stay quiet. The
				// scheduler tells the owner after 3 consecutive failures. Only a non-transient
				// failure alerts the operator at once, and never for a dry run.
				if (ex is ProviderException { IsTransient: true } transient)
					BackoffAccounts(transient.RetryAfterSeconds, ex.Message);
				else if (!_dryRun && new SyncPair().shouldAlert(_pair.pairID))
					Email.SendAlert($"MT-CalSync FAILED (pair {_pair.pairID})", FailureAlertBody(ex));
				return run;
			}
			finally
			{
				if (!_dryRun) lockRow.release();
			}
		}

		// Often read on a phone: lead with what's wrong and what happens next, and keep the
		// stack trace at the end for a bug report.
		private string FailureAlertBody(Exception ex) =>
			$"Sync pair {_pair.pairID} (\"{_pair.name}\") failed at {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC.\n\n" +
			$"{ex.Message}\n\n" +
			"The pair tries again on its normal schedule, spacing the attempts out while it keeps failing. " +
			$"To see its recent runs, use `history --pair {_pair.pairID}` on the worker CLI.\n\n" +
			"Technical details, for a bug report:\n" + ex;

		// Backs off this pair's delegated accounts after a transient provider condition. The
		// message's op prefix ("graph." / "google.") names the throttled side; without one,
		// both sides back off, which costs at most one cycle.
		private void BackoffAccounts(int? retryAfterSeconds, string message)
		{
			try
			{
				var until = DateTime.UtcNow.AddSeconds(Math.Max(retryAfterSeconds ?? 0, 120));
				bool graphSide = message.StartsWith("graph.", StringComparison.Ordinal);
				bool googleSide = message.StartsWith("google.", StringComparison.Ordinal);
				bool both = !graphSide && !googleSide;

				foreach (long connId in new[] { _pair.leftConnectionID, _pair.rightConnectionID })
				{
					var conn = new ProviderConnection().getById(connId);
					if (conn.authKind != AuthKinds.DelegatedOauth || conn.oauthAccountID <= 0) continue;
					if (!both && ((conn.provider == Providers.M365 && !graphSide) || (conn.provider == Providers.Google && !googleSide)))
						continue;
					new OAuthAccount().setBackoffUntil(conn.oauthAccountID, until);
					Common.audit($"pair={_pair.pairID} op=throttle account={conn.oauthAccountID} backoffUntil={until:HH:mm:ss}Z");
				}
			}
			catch (Exception ex) { Common.writeToLog("ERROR BackoffAccounts:", ex); }
		}

		// Asks the pair's owner to reconnect, at most once per account per 24h.
		private void NotifyOwnerReauth(NeedsReauthException nre)
		{
			try
			{
				var account = new OAuthAccount().getById(nre.OAuthAccountId);
				if (account.oauthAccountID == 0) return;
				if (account.lastReauthNotifyAt.HasValue && account.lastReauthNotifyAt.Value > DateTime.UtcNow.AddHours(-24)) return;

				// The host decides who is told and how; the engine owns the 24h dedupe, and
				// stamps it only when a notification went out.
				if (OwnerNotifier.Current.ReauthNeeded(account, nre.Provider))
					account.recordReauthNotify(account.oauthAccountID);
			}
			catch (Exception ex) { Common.writeToLog("ERROR NotifyOwnerReauth:", ex); }
		}

		private void BuildProviders()
		{
			var connL = new ProviderConnection().getById(_pair.leftConnectionID);
			var connR = new ProviderConnection().getById(_pair.rightConnectionID);
			bool series = _pair.SeriesMode;
			_providers[Providers.M365] = ProviderFactory.Create(connL, series);
			_providers[Providers.Google] = ProviderFactory.Create(connR, series);
		}

		private bool DueForFullResync(SyncState state, DateTime now)
		{
			if (!state.HasToken) return true;
			if (state.lastFullResyncAt == null) return true;
			return now.Hour == _pair.fullResyncHour && state.lastFullResyncAt.Value.Date < now.Date;
		}

		// A stored token is good while the range it was minted over covers the live window.
		// Otherwise the run lists in full, and a full Graph list never reports a deletion.
		private static bool TokenCovers(SyncState state, RollingWindow live) =>
			state.HasToken && state.windowStart.HasValue && state.windowEnd.HasValue
			&& state.windowStart.Value <= live.StartUtc && state.windowEnd.Value >= live.EndUtc;

		// One side's changes. A full list shows only what exists now, so it can't report a
		// deletion. When a full list is due while a token is held, read the token first and
		// keep the deletions it reports.
		private async Task<ChangeSet> PullChanges(string side, SyncState state, bool forceFull)
		{
			var provider = _providers[side];
			if (!state.HasToken)
				return await provider.GetChangesAsync(_tokenRange, state, true);

			ChangeSet pending;
			try { pending = await provider.GetChangesAsync(_tokenRange, state, false); }
			catch (ProviderException pe) when (!pe.IsTransient)
			{
				// A refused token is abandoned for a full list, as providers already do for a 410.
				// If the calendar itself is the problem, the full list fails and says so.
				Common.writeToLog($"pair={_pair.pairID} {side} token refused ({pe.Message}); listing in full instead.");
				return await provider.GetChangesAsync(_tokenRange, state, true);
			}
			if (!forceFull || pending.WasFullSync) return pending;   // WasFullSync: it had expired, so that read was the full list

			var full = await provider.GetChangesAsync(_tokenRange, state, true);
			var listed = new HashSet<string>(full.Items.Select(i => i.Id));
			full.Items.AddRange(pending.Items.Where(i => i.IsDeleted && !listed.Contains(i.Id)));
			return full;
		}

		// Classify one change from a source side.
		private async Task<SyncOp?> Classify(string side, RemoteEvent c, SyncRun run)
		{
			if (string.IsNullOrEmpty(c.Id)) { run.skippedCount++; return null; }

			// Skip dead-lettered items so one poison event can't fail the whole pair.
			if (_dl.hasOpen(_pair.pairID, side, c.Id)) { run.skippedCount++; return null; }

			// A series-mode exception (modified or cancelled occurrence). Its echo safety comes
			// from the series master, not the instance id: if the master is our mirror, so is
			// the exception.
			if (_pair.SeriesMode && c.IsRecurringInstance)
				return ClassifyException(side, c, run);

			// A mapping queued earlier in this run counts: the other side may already have linked
			// this event.
			var m = _map.findBySideKey(_pair.pairID, side, Common.Sha256Hex(c.Id))
				?? _deferredMappings.FirstOrDefault(d => d.EventIdForSide(side) == c.Id);

			// A tombstoned mapping is finished: its mirror was deleted, or a teardown kept it. A
			// repeated deletion (a full Google list still shows cancelled events) sends nothing.
			if (m != null && m.status == "tombstoned" && c.IsDeleted) { run.skippedCount++; return null; }

			// Exchange reissues an event's id on some edits and accepts, so the side-key lookup
			// can miss an event already mirrored, and a Create would duplicate the mirror. The
			// iCalUId survives the reissue: recover the mapping by it and re-key it to the new id.
			// Native origin-side singles and masters only; echoes and deletions have no uid.
			if (m == null && !c.IsDeleted && !c.IsRecurringInstance && !string.IsNullOrEmpty(c.ICalUid)
				&& !c.Stamp.IsOurMirrorOn(side, _pair.pairID))
			{
				var churned = _map.findActiveByOriginUid(_pair.pairID, side, c.ICalUid);
				if (churned != null && churned.originProvider == side)
				{
					Common.audit($"pair={_pair.pairID} op=rekey side={side} mapping={churned.mappingID} " +
						$"oldId={churned.EventIdForSide(side)} newId={c.Id} uid={c.ICalUid} result=ok");
					churned.SetSide(side, c.Id, c.ICalUid, c.Etag);
					Defer(churned);
					run.adoptedCount++;
					m = churned;
				}
			}

			// Echo: our own mirror coming back (stamped, or the mapping's origin is the other side).
			bool isEcho = c.Stamp.IsOurMirrorOn(side, _pair.pairID) || (m != null && m.originProvider != side);
			if (isEcho) return HandleEcho(side, c, m, run);

			// Another pair's mirror in a shared calendar is never re-mirrored here. A delta read
			// doesn't return the stamp, so the mapping table is the signal. The series-master id
			// catches an expanded occurrence of another series-mode pair's mirror master.
			if (m == null &&
				(_map.isMirrorInAnotherPair(_pair.pairID, side, Common.Sha256Hex(c.Id)) ||
				 (!string.IsNullOrEmpty(c.SeriesMasterId) &&
				  _map.isMirrorInAnotherPair(_pair.pairID, side, Common.Sha256Hex(c.SeriesMasterId)))))
			{ run.skippedCount++; return null; }

			// Native source change.
			if (c.IsDeleted)
			{
				if (m != null && m.originProvider == side)
					return new SyncOp { Kind = OpKind.Delete, SrcSide = side, DstSide = Providers.Other(side), SourceId = c.Id, Mapping = m, Source = c };
				run.skippedCount++; return null;   // an unmapped event's deletion: nothing to mirror
			}

			var units = _projection.Project(c, _pair, _window);
			if (units.Count == 0) { run.skippedCount++; return null; }
			var u = units[0];   // one unit per source event (single or series master)

			// Rolling-window filter. A series master is exempt: it may start before the window
			// while the series runs into it.
			if (u.UnitKind != UnitKinds.SeriesMaster && !_window.Contains(u.StartUtc)) { run.skippedCount++; return null; }

			if (m != null)   // existing mapping where `side` is the origin
			{
				if (u.Hash == m.projectedHash) { run.skippedCount++; return null; }   // nothing mirrored has changed
				return new SyncOp { Kind = OpKind.Update, SrcSide = side, DstSide = Providers.Other(side), SourceId = c.Id, Unit = u, Source = c, Mapping = m };
			}

			// No mapping: look for an existing mirror on the destination before creating one.
			string dst = Providers.Other(side);
			RemoteEvent? existing = await _providers[dst].FindByStampAsync(_pair.pairID, u.OriginId);
			if (existing == null && !string.IsNullOrEmpty(u.OriginICalUid))
				existing = await _providers[dst].FindByICalUidAsync(u.OriginICalUid);

			if (existing != null)
			{
				// Adopt: link `c` to the existing mirror, and update it if stale.
				var adopted = BuildMapping(side, c, dst, existing, u);
				adopted.projectedHash = existing.Stamp.Managed ? existing.Stamp.Hash : u.Hash;
				adopted.mirrorVersion = existing.Stamp.Version;
				Defer(adopted);
				run.adoptedCount++;
				if (u.Hash != adopted.projectedHash)
					return new SyncOp { Kind = OpKind.Update, SrcSide = side, DstSide = dst, SourceId = c.Id, Unit = u, Source = c, Mapping = adopted };
				return null;
			}

			return new SyncOp { Kind = OpKind.Create, SrcSide = side, DstSide = dst, SourceId = c.Id, Unit = u, Source = c };
		}

		// A modified or cancelled occurrence, applied as an override on the mirror master's
		// matching instance. The mirror master id is resolved at apply time, since the master's
		// op may run earlier in the same plan. Echo safety comes from the master, so the
		// override's own stamp isn't needed for correctness.
		private SyncOp? ClassifyException(string side, RemoteEvent c, SyncRun run)
		{
			string masterId = c.SeriesMasterId ?? string.Empty;
			if (string.IsNullOrEmpty(masterId)) { run.skippedCount++; return null; }

			var masterMap = _map.findBySideKey(_pair.pairID, side, Common.Sha256Hex(masterId));
			// The master on `side` is our mirror, so this exception is an echo.
			if (masterMap != null && masterMap.originProvider != side) { run.echoSkippedCount++; return null; }
			// The master is another pair's mirror in a shared calendar: not ours to propagate.
			if (masterMap == null && _map.isMirrorInAnotherPair(_pair.pairID, side, Common.Sha256Hex(masterId)))
			{ run.skippedCount++; return null; }

			string dst = Providers.Other(side);
			DateTime origStart = c.OccurrenceOriginalStartUtc ?? c.StartUtc;

			if (c.IsDeleted)
				return new SyncOp { Kind = OpKind.InstanceCancel, SrcSide = side, DstSide = dst, SourceId = c.Id, OriginMasterId = masterId, OrigStartUtc = origStart, Source = c };

			var units = _projection.Project(c, _pair, _window);
			if (units.Count == 0) { run.skippedCount++; return null; }
			var u = units[0];
			if (!_window.Contains(u.StartUtc)) { run.skippedCount++; return null; }   // moved out of window

			// An unchanged exception is a no-op.
			var exMap = _map.findByOccurrence(_pair.pairID, side, masterId, origStart);
			if (exMap != null && exMap.projectedHash == u.Hash) { run.skippedCount++; return null; }

			return new SyncOp { Kind = OpKind.InstanceUpsert, SrcSide = side, DstSide = dst, SourceId = c.Id, OriginMasterId = masterId, OrigStartUtc = origStart, Unit = u, Source = c, Mapping = exMap };
		}

		// `c` is our own mirror coming back on `side`. Mirrors are recognised by stamp or
		// mapping, never by re-projecting their provider-normalised content, which would
		// ping-pong on body/HTML normalisation. A changed etag is a provider bump: refresh it
		// and skip. A human edit to a mirror stays until the next origin change overwrites it.
		private SyncOp? HandleEcho(string side, RemoteEvent c, EventMapping? m, SyncRun run)
		{
			if (m == null)
			{
				// Stamped as ours with no mapping (a restored database, say): adopt from the stamp.
				var rebuilt = new EventMapping
				{
					pairID = _pair.pairID,
					originProvider = string.IsNullOrEmpty(c.Stamp.OriginSystem) ? Providers.Other(side) : c.Stamp.OriginSystem,
					projectedHash = c.Stamp.Hash,
					originContentHash = c.Stamp.Hash,
					mirrorVersion = c.Stamp.Version
				};
				rebuilt.SetSide(side, c.Id, c.ICalUid, c.Etag);   // the mirror side
				Defer(rebuilt);
				run.adoptedCount++; run.echoSkippedCount++;
				return null;
			}

			if (!_dryRun && !string.IsNullOrEmpty(c.Etag) && m.EtagForSide(side) != c.Etag)
				m.refreshMirrorEtag(side, c.Etag);   // provider bump: converge without a write
			run.echoSkippedCount++;
			return null;
		}

		// Build a fresh mapping linking an origin event and its (existing) mirror.
		private EventMapping BuildMapping(string originSide, RemoteEvent origin, string mirrorSide, RemoteEvent mirror, ProjectedUnit u)
		{
			var m = new EventMapping
			{
				pairID = _pair.pairID,
				originProvider = originSide,
				unitKind = u.UnitKind,
				originSeriesKey = u.OriginSeriesKey,
				occurrenceOriginalStart = u.OccurrenceOriginalStartUtc,
				projectedHash = u.Hash,
				originContentHash = u.Hash,
				lastSyncDirection = originSide + "->" + mirrorSide
			};
			m.SetSide(originSide, origin.Id, origin.ICalUid, origin.Etag);
			m.SetSide(mirrorSide, mirror.Id, mirror.ICalUid, mirror.Etag);
			return m;
		}

		// Apply with retry, then dead-letter.
		private async Task ApplyWithRetry(SyncOp op, SyncRun run)
		{
			int attempts = 0;
			while (true)
			{
				try { await ApplyOp(op, run); return; }
				catch (ProviderException pe)
				{
					if (!pe.IsTransient || attempts >= 4)
					{
						_dl.record(_pair.pairID, op.SrcSide, op.SourceId, op.Kind.ToString().ToLower(),
							op.Unit?.Subject ?? string.Empty, pe.Code, pe.Message);
						run.deadLetteredCount++;
						Common.audit($"pair={_pair.pairID} op={op.Kind.ToString().ToLower()} side={op.DstSide} oid={op.SourceId} result=dead-letter code={pe.Code}");
						return;
					}
					attempts++;
					int delay = pe.RetryAfterSeconds ?? (int)Math.Pow(2, attempts);
					await Task.Delay(Math.Min(30, Math.Max(1, delay)) * 1000);
				}
				// A dead grant fails every remaining op the same way. Let it end the run as
				// skipped_auth, which asks the owner to reconnect, rather than dead-letter each op.
				catch (NeedsReauthException) { throw; }
				catch (Exception ex)
				{
					_dl.record(_pair.pairID, op.SrcSide, op.SourceId, op.Kind.ToString().ToLower(), op.Unit?.Subject ?? string.Empty, "unhandled", ex.Message);
					run.deadLetteredCount++;
					Common.writeToLog($"Unhandled apply error pair={_pair.pairID} op={op.Kind}:", ex);
					return;
				}
			}
		}

		private async Task ApplyOp(SyncOp op, SyncRun run)
		{
			var dst = _providers[op.DstSide];
			switch (op.Kind)
			{
				case OpKind.Create:
				{
					long version = 1;
					var refr = await dst.CreateAsync(op.Unit!, _pair.pairID, version);
					var m = new EventMapping
					{
						pairID = _pair.pairID,
						originProvider = op.SrcSide,
						unitKind = op.Unit!.UnitKind,
						originSeriesKey = op.Unit.OriginSeriesKey,
						occurrenceOriginalStart = op.Unit.OccurrenceOriginalStartUtc,
						projectedHash = op.Unit.Hash,
						originContentHash = op.Unit.Hash,
						mirrorVersion = version,
						lastSyncDirection = op.SrcSide + "->" + op.DstSide
					};
					m.SetSide(op.SrcSide, op.Source!.Id, op.Source.ICalUid, op.Source.Etag);
					m.SetSide(op.DstSide, refr.Id, refr.ICalUid, refr.Etag);
					m.save();
					run.createdCount++;
					Common.audit($"pair={_pair.pairID} op=create side={op.DstSide} origin={op.SrcSide} oid={op.SourceId} mirror={refr.Id} hash={op.Unit.Hash[..8]} result=ok");
					break;
				}
				case OpKind.Update:
				{
					var m = op.Mapping!;
					long version = m.mirrorVersion + 1;
					string mirrorId = m.EventIdForSide(op.DstSide);
					var live = await dst.GetAsync(mirrorId);
					if (live != null && MirrorGuard.RefusalReason(live, m.ICalUidForSide(op.SrcSide)) is { } why)
					{
						// Remember the origin's new content so the refusal isn't re-planned every run.
						m.SetSide(op.SrcSide, op.Source!.Id, op.Source.ICalUid, op.Source.Etag);
						m.projectedHash = op.Unit!.Hash; m.originContentHash = op.Unit.Hash;
						m.save();
						run.skippedCount++;
						Common.audit($"pair={_pair.pairID} op=update side={op.DstSide} origin={op.SrcSide} oid={op.SourceId} mirror={mirrorId} result=refused reason=\"{why}\"");
						break;
					}
					var refr = await dst.UpdateAsync(mirrorId, m.EtagForSide(op.DstSide), op.Unit!, _pair.pairID, version);
					m.SetSide(op.DstSide, refr.Id, refr.ICalUid, refr.Etag);
					m.SetSide(op.SrcSide, op.Source!.Id, op.Source.ICalUid, op.Source.Etag);
					m.projectedHash = op.Unit!.Hash; m.originContentHash = op.Unit.Hash;
					m.mirrorVersion = version; m.lastSyncDirection = op.SrcSide + "->" + op.DstSide;
					m.save();
					run.updatedCount++;
					Common.audit($"pair={_pair.pairID} op=update side={op.DstSide} origin={op.SrcSide} oid={op.SourceId} mirror={refr.Id} hash={op.Unit.Hash[..8]} result=ok");
					break;
				}
				case OpKind.Delete:
				{
					var m = op.Mapping!;
					string mirrorId = m.EventIdForSide(op.DstSide);
					var live = string.IsNullOrEmpty(mirrorId) ? null : await dst.GetAsync(mirrorId);
					if (live != null && MirrorGuard.RefusalReason(live, m.ICalUidForSide(op.SrcSide)) is { } why)
					{
						// Unlink without deleting: the other event stays where it is.
						m.tombstone();
						run.skippedCount++;
						Common.audit($"pair={_pair.pairID} op=delete side={op.DstSide} origin={op.SrcSide} oid={op.SourceId} mirror={mirrorId} result=refused reason=\"{why}\"");
						break;
					}
					if (live != null && !live.IsDeleted)
						await dst.DeleteAsync(mirrorId, m.EtagForSide(op.DstSide));
					m.tombstone();
					run.deletedCount++;
					Common.audit($"pair={_pair.pairID} op=delete side={op.DstSide} origin={op.SrcSide} oid={op.SourceId} mirror={mirrorId} result=ok");
					break;
				}
				case OpKind.InstanceUpsert:
				{
					// Override one instance of the mirror master, whose id is resolved now because
					// its create or update ran earlier in this plan.
					string mirrorMasterId = MirrorMasterId(op.SrcSide, op.DstSide, op.OriginMasterId);
					if (string.IsNullOrEmpty(mirrorMasterId))
						throw new ProviderException("exception: series master not mapped yet", "no-master", false);
					if (await RefuseSeriesWrite(dst, mirrorMasterId, op, run)) break;
					long version = (op.Mapping?.mirrorVersion ?? 0) + 1;
					var refr = await dst.UpsertInstanceAsync(mirrorMasterId, op.OrigStartUtc, op.Unit!, _pair.pairID, version);
					var em = op.Mapping ?? new EventMapping
					{
						pairID = _pair.pairID, originProvider = op.SrcSide, unitKind = UnitKinds.Occurrence,
						originSeriesKey = op.OriginMasterId, occurrenceOriginalStart = op.OrigStartUtc
					};
					em.SetSide(op.SrcSide, op.Source!.Id, op.Source.ICalUid, op.Source.Etag);
					em.SetSide(op.DstSide, refr.Id, refr.ICalUid, refr.Etag);
					em.projectedHash = op.Unit!.Hash; em.originContentHash = op.Unit.Hash;
					em.mirrorVersion = version; em.lastSyncDirection = op.SrcSide + "->" + op.DstSide;
					em.save();
					run.updatedCount++;
					Common.audit($"pair={_pair.pairID} op=instance-upsert side={op.DstSide} origin={op.SrcSide} master={mirrorMasterId} origStart={op.OrigStartUtc:u} result=ok");
					break;
				}
				case OpKind.InstanceCancel:
				{
					string mirrorMasterId = MirrorMasterId(op.SrcSide, op.DstSide, op.OriginMasterId);
					if (!string.IsNullOrEmpty(mirrorMasterId) && await RefuseSeriesWrite(dst, mirrorMasterId, op, run)) break;
					if (!string.IsNullOrEmpty(mirrorMasterId))
						await dst.CancelInstanceAsync(mirrorMasterId, op.OrigStartUtc);
					_map.findByOccurrence(_pair.pairID, op.SrcSide, op.OriginMasterId, op.OrigStartUtc)?.tombstone();
					run.deletedCount++;
					Common.audit($"pair={_pair.pairID} op=instance-cancel side={op.DstSide} origin={op.SrcSide} master={mirrorMasterId} origStart={op.OrigStartUtc:u} result=ok");
					break;
				}
			}
		}

		// The mirror-side id of an origin series master (resolved from the master mapping).
		private string MirrorMasterId(string srcSide, string dstSide, string originMasterId)
		{
			var masterMap = _map.findBySideKey(_pair.pairID, srcSide, Common.Sha256Hex(originMasterId));
			return masterMap?.EventIdForSide(dstSide) ?? string.Empty;
		}

		// An occurrence is changed through its series master, so the master must be our mirror.
		private async Task<bool> RefuseSeriesWrite(ICalendarProvider dst, string mirrorMasterId, SyncOp op, SyncRun run)
		{
			var master = await dst.GetAsync(mirrorMasterId);
			if (master == null || MirrorGuard.RefusalReason(master) is not { } why) return false;
			run.skippedCount++;
			Common.audit($"pair={_pair.pairID} op={op.Kind.ToString().ToLower()} side={op.DstSide} origin={op.SrcSide} master={mirrorMasterId} origStart={op.OrigStartUtc:u} result=refused reason=\"{why}\"");
			return true;
		}
	}
}
