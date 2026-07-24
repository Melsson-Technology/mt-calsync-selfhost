namespace Core.MTCalSync
{
	// Lease-based per-pair advisory lock (compare-and-set). Used instead of MySQL
	// GET_LOCK() because DataAccess opens a fresh connection per call, so a
	// session-scoped lock can't span a multi-second run. Crash-safe via lease expiry.
	public class SyncLock
	{
		private readonly long _pairID;
		private readonly string _owner;

		public SyncLock(long pairID)
		{
			_pairID = pairID;
			// host:pid:guid — unique per run so release only clears our own lease.
			string owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
			_owner = owner.Length > 120 ? owner.Substring(0, 120) : owner;
		}

		public string Owner => _owner;

		// Try to acquire. Returns true iff we won the lease. leaseSeconds should
		// comfortably exceed the max run time (a crashed run's lock frees after it).
		public bool tryAcquire(int leaseSeconds = 600)
		{
			var oDA = new DataAccess();
			// Ensure the row exists (no-op if present).
			oDA.insertData("insert ignore into sync_lock (pairID) values (@p)",
				new Dictionary<string, object> { { "@p", _pairID } });
			// CAS: claim only if free or lease expired. leaseSeconds is a code constant
			// (not user input), so inlining it into the INTERVAL is safe.
			int secs = Math.Max(30, leaseSeconds);
			string sql =
				"update sync_lock set lockedBy=@me, lockedAt=NOW(), leaseExpiresAt=NOW() + INTERVAL " + secs + " SECOND " +
				"where pairID=@p and (leaseExpiresAt is null or leaseExpiresAt < NOW())";
			var p = new Dictionary<string, object> { { "@me", _owner }, { "@p", _pairID } };
			return oDA.updateData(sql, p);
		}

		public void release()
		{
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@me", _owner }, { "@p", _pairID } };
			oDA.updateData("update sync_lock set leaseExpiresAt=NULL, lockedBy=NULL where pairID=@p and lockedBy=@me", p);
		}
	}
}
