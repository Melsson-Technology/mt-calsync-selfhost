namespace Core.MTCalSync
{
	// The per-minute tick. RunDue takes pairs whose nextRunAt has arrived, skips those the
	// sync gate refuses or whose accounts are flagged or throttled, and runs the rest under
	// a concurrency cap. One pair's failure never affects another.
	//
	// Rescheduling after each run:
	//   healthy → now + interval + 0–60s jitter, so pairs created together spread out
	//   failed  → interval · 2^consecutiveFailures, capped at 1h
	//   gated   → now + interval, a cheap re-check that resumes once the gate clears
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
				// Sync gate, looked up once per owner per tick. The default allows every pair;
				// hosts that embed the engine can replace it.
				if (!eligibilityCache.TryGetValue(pair.customerID, out var canSync))
					eligibilityCache[pair.customerID] = canSync = SyncGate.Current.CanSync(pair.customerID);
				// A dry-run tick leaves schedules and alerts alone, so a preview never delays a
				// real sync.
				if (!canSync)
				{
					if (!dryRun) pairEntity.updateNextRun(pair.pairID, now.AddSeconds(pair.runIntervalSeconds));
					continue;
				}

				// A flagged (needs_reauth) or throttled account pauses every pair that uses it.
				var gate = AccountGate(pair, accountCache, now);
				if (gate != null)
				{
					if (!dryRun) pairEntity.updateNextRun(pair.pairID, gate.Value);
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
					if (!dryRun) Reschedule(pair, run);
					if (run != null && (run.status == "failed" || run.status == "aborted_circuit_breaker"))
						Interlocked.Exchange(ref worst, 2);
					if (!dryRun && run != null && run.status == "failed")
						MaybeAlertOwnerPersistentFailure(pair, run);
				}
				catch (Exception ex)
				{
					// The engine reports its own failures; this stops a scheduler-level error in
					// one pair from ending the batch.
					Common.writeToLog($"ERROR SyncScheduler pair {pair.pairID}:", ex);
					if (!dryRun) pairEntity.updateNextRun(pair.pairID, DateTime.UtcNow.AddSeconds(pair.runIntervalSeconds));
				}
				finally { gateSem.Release(); }
			}).ToList();
			await Task.WhenAll(tasks);
			return worst;
		}

		// Non-null: when to look at this pair again instead of running it now.
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
				// Exponential backoff, capped at an hour. The engine records the failure count
				// per side in sync_state.
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

		// A pair that keeps failing is an outage the owner can see, so the owner is told after
		// this many consecutive failures, at most once per pair per day (sync_pair.lastAlertAt).
		public const int OwnerAlertAfterFailures = 3;

		private static void MaybeAlertOwnerPersistentFailure(SyncPair pair, SyncRun run)
		{
			try
			{
				if (MaxConsecutiveFailures(pair) < OwnerAlertAfterFailures) return;
				if (!new SyncPair().shouldAlert(pair.pairID)) return;
				OwnerNotifier.Current.PersistentFailure(pair, run);
			}
			catch (Exception ex) { Common.writeToLog("ERROR MaybeAlertOwnerPersistentFailure:", ex); }
		}
	}
}
