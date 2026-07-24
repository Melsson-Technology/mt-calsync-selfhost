namespace Core.MTCalSync
{
	// The multi-tenant tick. The systemd timer fires every minute; RunDue claims
	// only pairs whose nextRunAt has arrived, filters out pairs whose customer
	// isn't entitled or whose accounts are flagged/throttled, and runs the rest
	// under a small concurrency cap. Per-pair failures never touch other pairs.
	//
	// Rescheduling after each run:
	//   healthy   → now + runIntervalSeconds + jitter(0–60s)  (jitter de-clumps
	//               pairs created at the same moment)
	//   failed    → exponential: interval · 2^consecutiveFailures, capped at 1h
	//   gated     → now + interval (cheap re-check; resumes automatically when
	//               entitlement/account recovers)
	public static class SyncScheduler
	{
		public static async Task<int> RunDue(string trigger, bool dryRun = false, bool force = false, bool fullResync = false)
		{
			var due = new SyncPair().listDue();
			if (due.Count == 0) return 0;

			var pairEntity = new SyncPair();
			var eligibilityCache = new Dictionary<long, bool>();
			var accountCache = new Dictionary<long, OAuthAccount>();
			var runnable = new List<SyncPair>();
			var now = DateTime.UtcNow;

			foreach (var pair in due)
			{
				// Sync-eligibility gate (one lookup per customer per tick). Self-host
				// allows all; the hosted layer installs a subscription-aware gate.
				if (!eligibilityCache.TryGetValue(pair.customerID, out var canSync))
					eligibilityCache[pair.customerID] = canSync = SyncGate.Current.CanSync(pair.customerID);
				if (!canSync)
				{
					pairEntity.updateNextRun(pair.pairID, now.AddSeconds(pair.runIntervalSeconds));
					continue;
				}

				// Account health gate: a flagged (needs_reauth) or throttled account
				// pauses every pair riding on it.
				var gate = AccountGate(pair, accountCache, now);
				if (gate != null)
				{
					pairEntity.updateNextRun(pair.pairID, gate.Value);
					continue;
				}

				runnable.Add(pair);
			}
			if (runnable.Count == 0) return 0;

			int worst = 0;
			var gateSem = new SemaphoreSlim(Math.Max(1, Settings.WorkerMaxConcurrency));
			var tasks = runnable.Select(async pair =>
			{
				await gateSem.WaitAsync();
				try
				{
					var run = await new SyncEngine(pair, dryRun, force, fullResync).Run(trigger);
					Reschedule(pair, run);
					if (run != null && (run.status == "failed" || run.status == "aborted_circuit_breaker"))
						Interlocked.Exchange(ref worst, 2);
					if (run != null && run.status == "failed")
						MaybeAlertOwnerPersistentFailure(pair, run);
				}
				catch (Exception ex)
				{
					// The engine reports its own failures; this catches scheduler-level
					// surprises so one pair can't take down the batch.
					Common.writeToLog($"ERROR SyncScheduler pair {pair.pairID}:", ex);
					pairEntity.updateNextRun(pair.pairID, DateTime.UtcNow.AddSeconds(pair.runIntervalSeconds));
				}
				finally { gateSem.Release(); }
			}).ToList();
			await Task.WhenAll(tasks);
			return worst;
		}

		// Non-null result = when to look at this pair again instead of running it.
		private static DateTime? AccountGate(SyncPair pair, Dictionary<long, OAuthAccount> cache, DateTime now)
		{
			foreach (long connId in new[] { pair.leftConnectionID, pair.rightConnectionID })
			{
				var conn = new ProviderConnection().getById(connId);
				if (conn.authKind != AuthKinds.DelegatedOauth || conn.oauthAccountID <= 0) continue;

				if (!cache.TryGetValue(conn.oauthAccountID, out var account))
					cache[conn.oauthAccountID] = account = new OAuthAccount().getById(conn.oauthAccountID);

				if (!account.isConnected)
					return now.AddSeconds(pair.runIntervalSeconds);
				if (account.backoffUntil.HasValue && account.backoffUntil.Value > now)
					return account.backoffUntil.Value.AddSeconds(Random.Shared.Next(0, 30));
			}
			return null;
		}

		private static void Reschedule(SyncPair pair, SyncRun? run)
		{
			var now = DateTime.UtcNow;
			int interval = pair.runIntervalSeconds;
			DateTime next;

			if (run != null && run.status == "failed")
			{
				// Exponential backoff on repeated failure, capped at an hour. The
				// counter lives in sync_state (recorded by the engine per side).
				int failures = MaxConsecutiveFailures(pair);
				double factor = Math.Pow(2, Math.Clamp(failures, 0, 6));
				next = now.AddSeconds(Math.Min(interval * factor, 3600)).AddSeconds(Random.Shared.Next(0, 60));
			}
			else
			{
				next = now.AddSeconds(interval + Random.Shared.Next(0, 60));
			}
			new SyncPair().updateNextRun(pair.pairID, next);
		}

		private static int MaxConsecutiveFailures(SyncPair pair)
		{
			int worst = 0;
			foreach (var side in Directions.SourceSides(pair.direction))
			{
				var state = new SyncState().getByPairProvider(pair.pairID, side);
				worst = Math.Max(worst, state.consecutiveFailures);
			}
			return worst;
		}

		// A pair that keeps failing is a user-visible outage: tell the OWNER (not
		// just the operator) once things look persistent, throttled to one email
		// per pair per day via sync_pair.lastAlertAt.
		private static void MaybeAlertOwnerPersistentFailure(SyncPair pair, SyncRun run)
		{
			try
			{
				if (MaxConsecutiveFailures(pair) < 3) return;
				if (!new SyncPair().shouldAlert(pair.pairID)) return;
				OwnerNotifier.Current.PersistentFailure(pair, run);
			}
			catch (Exception ex) { Common.writeToLog("ERROR MaybeAlertOwnerPersistentFailure:", ex); }
		}
	}
}
