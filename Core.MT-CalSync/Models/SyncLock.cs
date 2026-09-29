namespace Core.MTCalSync
{
	// Per-pair lease lock, taken by compare-and-set. MySQL GET_LOCK() is session-scoped and
	// DataAccess opens a connection per call, so it can't span a run. A crashed run's
	// lock frees when its lease expires.
	public class SyncLock
	{
		private readonly long _pairID;
		private readonly string _owner;

		public SyncLock(long pairID)
		{
			_pairID = pairID;
			// host:pid:guid, unique per run, so release only clears our own lease.
			string owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
			_owner = owner.Length > 120 ? owner.Substring(0, 120) : owner;
		}

		public string Owner => _owner;

		// Returns true if we won the lease. leaseSeconds should comfortably exceed the
		// longest run, since a live run's lease must not expire under it.
		public bool tryAcquire(int leaseSeconds = 600)
		{
			var oDA = new DataAccess();
			// Make sure the row exists.
			oDA.insertData("insert ignore into sync_lock (pairID) values (@p)",
				new Dictionary<string, object> { { "@p", _pairID } });
			// Claim only if free or expired. secs is an int, so inlining it into INTERVAL is safe.
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
