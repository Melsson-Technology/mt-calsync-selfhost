namespace Core.MTCalSync
{
	// The invariant core. One oneshot run over one sync_pair:
	//   lease → pull deltas per source side → classify (echo/native/delete) →
	//   plan ops → circuit-breaker → apply (retry/dead-letter) → persist tokens.
	// Governing invariant: a mirror is a read-only reflection of exactly one origin
	// event; the origin side always wins.
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
		private RollingWindow _window = new();
		private readonly Dictionary<string, ICalendarProvider> _providers = new();
		private readonly EventMapping _map = new();
		private readonly DeadLetter _dl = new();

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

			var lockRow = new SyncLock(_pair.pairID);
			if (!_dryRun && !lockRow.tryAcquire(600))
			{
				Common.audit($"pair={_pair.pairID} op=lock result=skipped_locked");
				var s = SyncRun.start(_pair.pairID, trigger, "incremental"); s.finish("skipped_locked"); return s;
			}

			var run = SyncRun.start(_pair.pairID, trigger, "incremental");
			var pendingTokens = new Dictionary<string, (string? token, bool wasFull)>();
			try
			{
				BuildProviders();

				// ── plan: pull + classify per source side ────────────────────
				var ops = new List<SyncOp>();
				foreach (var side in Directions.SourceSides(_pair.direction))
				{
					var state = new SyncState().getByPairProvider(_pair.pairID, side);
					bool forceFull = _fullResync || _force || DueForFullResync(state, now);
					if (forceFull) run.syncType = "full_resync";

					ChangeSet changes;
					try { changes = await _providers[side].GetChangesAsync(_window, state, forceFull); }
					catch (ProviderException pe)
					{
						state.recordFailure(pe.Message);
						throw;   // abort the run; tokens for this side are not advanced
					}

					if (side == Providers.M365) run.leftChanges += changes.Items.Count; else run.rightChanges += changes.Items.Count;
					pendingTokens[side] = (changes.NewToken, changes.WasFullSync);

					foreach (var c in changes.Items)
					{
						var op = await Classify(side, c, run);
						if (op != null) ops.Add(op);
					}
				}

				// Apply series masters/singles BEFORE their instance overrides — an
				// exception's mirror master must exist first (stable sort preserves order).
				ops = ops.OrderBy(o => o.IsInstanceOp ? 1 : 0).ToList();

				// ── circuit breaker ──────────────────────────────────────────
				int destructive = ops.Count(o => o.Kind == OpKind.Create || o.Kind == OpKind.Delete || o.Kind == OpKind.InstanceCancel);
				if (destructive > _pair.maxWritesPerRun && !_force)
				{
					string msg = $"Circuit breaker: {destructive} create/delete ops exceed maxWritesPerRun={_pair.maxWritesPerRun}. " +
								 "Nothing applied. Re-run with --force if this is expected.";
					Common.audit($"pair={_pair.pairID} op=circuit-breaker planned={destructive} result=aborted");
					run.errorText = msg; run.finish("aborted_circuit_breaker");
					if (new SyncPair().shouldAlert(_pair.pairID))
						Email.SendAlert($"MT-CalSync circuit breaker (pair {_pair.pairID})", msg + "\n\n" + run.Summary());
					return run;
				}

				// ── dry run: show the plan, touch nothing ────────────────────
				if (_dryRun)
				{
					Console.WriteLine($"DRY RUN — pair {_pair.pairID} ({_pair.name}): {ops.Count} op(s)");
					foreach (var o in ops)
					{
						string label = o.Kind == OpKind.InstanceCancel ? "(cancel occurrence)" : (o.Unit?.Subject ?? "(delete)");
						string when = o.IsInstanceOp ? o.OrigStartUtc.ToString("u") : (o.Unit?.StartUtc.ToString("u") ?? "");
						Console.WriteLine($"  {o.Kind,-14} {o.SrcSide}->{o.DstSide}  {label}  start={when}");
					}
					run.finish("success");
					return run;
				}

				// ── apply ────────────────────────────────────────────────────
				foreach (var op in ops) await ApplyWithRetry(op, run);

				// ── persist tokens only after a successful apply pass ────────
				foreach (var side in Directions.SourceSides(_pair.direction))
				{
					if (!pendingTokens.TryGetValue(side, out var pt)) continue;
					var state = new SyncState { pairID = _pair.pairID, provider = side };
					state.saveAfterRun(pt.token, _window, pt.wasFull);
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
				// A user's OAuth grant died (revoked/expired) — not a sync fault. File
				// the run as skipped_auth (no dead-letter, no admin alert) and nudge the
				// OWNER to reconnect, once per 24h.
				run.errorText = nre.Message;
				run.finish("skipped_auth");
				Common.audit($"pair={_pair.pairID} op=auth result=skipped_auth account={nre.OAuthAccountId}");
				NotifyOwnerReauth(nre);
				return run;
			}
			catch (Exception ex)
			{
				run.errorText = ex.Message;
				run.finish("failed");
				Common.writeToLog($"FATAL sync pair {_pair.pairID}:", ex);
				// Transient provider conditions — 429 throttling, 5xx / "request queue
				// full" overload, transport retry-exhaustion — are ACCOUNT-wide (quotas
				// are per account, not per pair) and self-healing. Park the affected
				// account(s) briefly and stay QUIET: the scheduler escalates to the
				// owner only once failures persist (>=3 consecutive), so a single
				// Microsoft/Google blip never pages anyone. Only a genuine
				// (non-transient) failure — a real bug — alerts the operator at once.
				if (ex is ProviderException { IsTransient: true } transient)
					BackoffAccounts(transient.RetryAfterSeconds, ex.Message);
				else if (new SyncPair().shouldAlert(_pair.pairID))
					Email.SendAlert($"MT-CalSync FAILED (pair {_pair.pairID})", ex.ToString());
				return run;
			}
			finally
			{
				if (!_dryRun) lockRow.release();
			}
		}

		// Set backoffUntil on this pair's delegated account(s) after a transient
		// provider condition (429 / 5xx / transport retry-exhaustion). The op prefix
		// in the message identifies the throttled side ("graph." / "google."); when
		// ambiguous, park both — over-caution just delays one cycle.
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

		// "Reconnect your account" email to the pair's owner, deduped to one per
		// account per 24h via lastReauthNotifyAt.
		private void NotifyOwnerReauth(NeedsReauthException nre)
		{
			try
			{
				var account = new OAuthAccount().getById(nre.OAuthAccountId);
				if (account.oauthAccountID == 0) return;
				if (account.lastReauthNotifyAt.HasValue && account.lastReauthNotifyAt.Value > DateTime.UtcNow.AddHours(-24)) return;

				// WHO/HOW is the hosting layer's call (self-host = operator SMTP; hosted =
				// per-owner template + ledger). WHEN — the 24h dedupe above — is ours; stamp
				// it only if a notification actually went out.
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

		// ── classify one change on source side ──────────────────────────────
		private async Task<SyncOp?> Classify(string side, RemoteEvent c, SyncRun run)
		{
			if (string.IsNullOrEmpty(c.Id)) { run.skippedCount++; return null; }

			// dead-letter: skip poison items so one can't fail the whole pair.
			if (_dl.hasOpen(_pair.pairID, side, c.Id)) { run.skippedCount++; return null; }

			// Series-mode exception (modified/cancelled single occurrence) — routed
			// specially. Echo-safety uses the SERIES MASTER relationship (independent of
			// instance-id stability): if the exception's master is our mirror, so is the
			// exception. Masters/singles fall through to the generic path below.
			if (_pair.SeriesMode && c.IsRecurringInstance)
				return ClassifyException(side, c, run);

			var m = _map.findBySideKey(_pair.pairID, side, Common.Sha256Hex(c.Id));

			// EntryID-churn recovery: Exchange reissues an event's id on some edits/accepts,
			// so the side-key lookup can miss an event we ALREADY mirror. Left unhandled it
			// falls through to Create and duplicates the mirror (edit→revert = 2 churns = 3
			// copies). The origin iCalUId survives that churn — recover the existing mapping
			// by it (native origin-side singles/masters only) and re-key it to the reissued id
			// so the normal update/no-op path below takes over. Skips our own mirrors (echo)
			// and @removed deltas (no uid).
			if (m == null && !c.IsDeleted && !c.IsRecurringInstance && !string.IsNullOrEmpty(c.ICalUid)
				&& !c.Stamp.IsOurMirrorOn(side, _pair.pairID))
			{
				var churned = _map.findActiveByOriginUid(_pair.pairID, side, c.ICalUid);
				if (churned != null && churned.originProvider == side)
				{
					Common.audit($"pair={_pair.pairID} op=rekey side={side} mapping={churned.mappingID} " +
						$"oldId={churned.EventIdForSide(side)} newId={c.Id} uid={c.ICalUid} result=ok");
					churned.SetSide(side, c.Id, c.ICalUid, c.Etag);
					churned.save();
					run.adoptedCount++;
					m = churned;
				}
			}

			// echo: our own mirror coming back (stamp inline, or mapping says origin!=side).
			bool isEcho = c.Stamp.IsOurMirrorOn(side, _pair.pairID) || (m != null && m.originProvider != side);
			if (isEcho) return HandleEcho(side, c, m, run);

			// cross-pair foreign mirror: this pair has no mapping for `c`, but another pair
			// wrote it into this (shared) destination calendar as ITS mirror. It's not a
			// native change here — never re-mirror it (would leak a sibling pair's events).
			// The delta read doesn't expand our provenance stamp, so the mapping table is the
			// reliable signal. Also test the series-master id in case `c` is an expanded
			// occurrence of a mirror master written by a series-mode pair.
			if (m == null &&
				(_map.isMirrorInAnotherPair(_pair.pairID, side, Common.Sha256Hex(c.Id)) ||
				 (!string.IsNullOrEmpty(c.SeriesMasterId) &&
				  _map.isMirrorInAnotherPair(_pair.pairID, side, Common.Sha256Hex(c.SeriesMasterId)))))
			{ run.skippedCount++; return null; }

			// native source change.
			if (c.IsDeleted)
			{
				if (m != null && m.originProvider == side)
					return new SyncOp { Kind = OpKind.Delete, SrcSide = side, DstSide = Providers.Other(side), SourceId = c.Id, Mapping = m, Source = c };
				run.skippedCount++; return null;   // deletion of an unmapped event — nothing to mirror
			}

			var units = _projection.Project(c, _pair, _window);
			if (units.Count == 0) { run.skippedCount++; return null; }
			var u = units[0];   // one unit per source event (single or series master)

			// rolling-window filter (Google returns a whole/bounded set; Graph is windowed).
			// Never window-filter a series master — its start may predate the window while
			// the series extends into it.
			if (u.UnitKind != UnitKinds.SeriesMaster && !_window.Contains(u.StartUtc)) { run.skippedCount++; return null; }

			if (m != null)   // existing mapping where `side` is the origin
			{
				if (u.Hash == m.projectedHash) { run.skippedCount++; return null; }   // no functional change / echo of our write
				return new SyncOp { Kind = OpKind.Update, SrcSide = side, DstSide = Providers.Other(side), SourceId = c.Id, Unit = u, Source = c, Mapping = m };
			}

			// no mapping → match-before-create ladder on the destination.
			string dst = Providers.Other(side);
			RemoteEvent? existing = await _providers[dst].FindByStampAsync(_pair.pairID, u.OriginId);
			if (existing == null && !string.IsNullOrEmpty(u.OriginICalUid))
				existing = await _providers[dst].FindByICalUidAsync(u.OriginICalUid);

			if (existing != null)
			{
				// adopt: link origin(side)=c and the existing mirror; update if stale.
				var adopted = BuildMapping(side, c, dst, existing, u);
				adopted.projectedHash = existing.Stamp.Managed ? existing.Stamp.Hash : u.Hash;
				adopted.mirrorVersion = existing.Stamp.Version;
				adopted.save();
				run.adoptedCount++;
				if (u.Hash != adopted.projectedHash)
					return new SyncOp { Kind = OpKind.Update, SrcSide = side, DstSide = dst, SourceId = c.Id, Unit = u, Source = c, Mapping = adopted };
				return null;
			}

			return new SyncOp { Kind = OpKind.Create, SrcSide = side, DstSide = dst, SourceId = c.Id, Unit = u, Source = c };
		}

		// Series-mode exception: a modified/cancelled single occurrence of a recurring
		// series. Applied as an override on the MIRROR master's matching instance. The
		// mirror master id is resolved at apply time (the master op may run earlier in the
		// same plan). Echo-safety is by the master relationship, so no per-instance stamp
		// tracking is required for correctness (the override is still stamped as hygiene).
		private SyncOp? ClassifyException(string side, RemoteEvent c, SyncRun run)
		{
			string masterId = c.SeriesMasterId ?? string.Empty;
			if (string.IsNullOrEmpty(masterId)) { run.skippedCount++; return null; }

			var masterMap = _map.findBySideKey(_pair.pairID, side, Common.Sha256Hex(masterId));
			// Echo: the master on `side` is OUR mirror → this exception is our echo too.
			if (masterMap != null && masterMap.originProvider != side) { run.echoSkippedCount++; return null; }
			// Cross-pair foreign mirror: the master belongs to another pair's mirror in this
			// shared calendar → this exception isn't ours to propagate.
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

			// change detection: unchanged exception → no-op.
			var exMap = _map.findByOccurrence(_pair.pairID, side, masterId, origStart);
			if (exMap != null && exMap.projectedHash == u.Hash) { run.skippedCount++; return null; }

			return new SyncOp { Kind = OpKind.InstanceUpsert, SrcSide = side, DstSide = dst, SourceId = c.Id, OriginMasterId = masterId, OrigStartUtc = origStart, Unit = u, Source = c, Mapping = exMap };
		}

		// Echo handling. `c` is our own mirror coming back on `side`.
		//
		// Loop-safety: we recognize mirrors STRUCTURALLY (stamp/mapping), never by
		// re-projecting their (provider-normalized) content. A changed etag is treated
		// as a benign provider bump — we refresh the stored etag and skip. A human edit
		// to a mirror is therefore not force-reverted on the spot; instead the next
		// change to the ORIGIN overwrites the mirror (origin wins, eventually) via the
		// reliable origin-vs-origin hash in Classify. This deliberately avoids the
		// body/HTML-normalization ping-pong that immediate content-revert would risk.
		private SyncOp? HandleEcho(string side, RemoteEvent c, EventMapping? m, SyncRun run)
		{
			if (m == null)
			{
				// Stamped as ours but no mapping (e.g. DB restored) → adopt from the stamp.
				var rebuilt = new EventMapping
				{
					pairID = _pair.pairID,
					originProvider = string.IsNullOrEmpty(c.Stamp.OriginSystem) ? Providers.Other(side) : c.Stamp.OriginSystem,
					projectedHash = c.Stamp.Hash,
					originContentHash = c.Stamp.Hash,
					mirrorVersion = c.Stamp.Version
				};
				rebuilt.SetSide(side, c.Id, c.ICalUid, c.Etag);   // the mirror side
				rebuilt.save();
				run.adoptedCount++; run.echoSkippedCount++;
				return null;
			}

			if (!string.IsNullOrEmpty(c.Etag) && m.EtagForSide(side) != c.Etag)
				m.refreshMirrorEtag(side, c.Etag);   // benign provider bump — converge, no write
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

		// ── apply with retry / dead-letter ──────────────────────────────────
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
					if (!string.IsNullOrEmpty(mirrorId))
						await dst.DeleteAsync(mirrorId, m.EtagForSide(op.DstSide));
					m.tombstone();
					run.deletedCount++;
					Common.audit($"pair={_pair.pairID} op=delete side={op.DstSide} origin={op.SrcSide} oid={op.SourceId} mirror={mirrorId} result=ok");
					break;
				}
				case OpKind.InstanceUpsert:
				{
					// Override a single instance of the mirror recurring master. The master
					// mirror id is resolved now (its create/update ran earlier this plan).
					string mirrorMasterId = MirrorMasterId(op.SrcSide, op.DstSide, op.OriginMasterId);
					if (string.IsNullOrEmpty(mirrorMasterId))
						throw new ProviderException("exception: series master not mapped yet", "no-master", false);
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
	}
}
