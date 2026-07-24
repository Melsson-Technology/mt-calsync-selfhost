using System.Data;

namespace Core.MTCalSync
{
	// A single calendar endpoint on one provider (M365 mailbox or Google user).
	// authKind selects the credential path: app_default (global app credentials —
	// operator connections) or delegated_oauth (per-user grant via oauthAccountID).
	public class ProviderConnection : @base
	{
		public long connectionID { get; set; }
		public long userID { get; set; }
		public long customerID { get; set; }
		public string provider { get; set; } = string.Empty;      // m365|google
		public string displayName { get; set; } = string.Empty;
		public string principalEmail { get; set; } = string.Empty;
		public string calendarId { get; set; } = "primary";
		public string m365TenantId { get; set; } = string.Empty;
		public string m365ClientId { get; set; } = string.Empty;
		public string credentialRef { get; set; } = string.Empty;
		public string authKind { get; set; } = AuthKinds.AppDefault;
		public long oauthAccountID { get; set; }
		public bool enabled { get; set; } = true;

		public ProviderConnection getById(long id)
		{
			var o = new ProviderConnection();
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@id", id } };
			try
			{
				var ds = oDA.execQuery("select * from provider_connection where connectionID = @id", "DATA", "DATA", p);
				if (ds.Tables[0].Rows.Count > 0) o = dataRowToObject(ds.Tables[0].Rows[0]);
			}
			catch (Exception ex) { Common.writeToLog("ERROR ProviderConnection.getById:", ex); }
			return o;
		}

		public List<ProviderConnection> listAll()
		{
			var list = new List<ProviderConnection>();
			var oDA = new DataAccess();
			try
			{
				var ds = oDA.execQuery("select * from provider_connection order by connectionID", "DATA", "DATA");
				foreach (DataRow r in ds.Tables[0].Rows) list.Add(dataRowToObject(r));
			}
			catch (Exception ex) { Common.writeToLog("ERROR ProviderConnection.listAll:", ex); }
			return list;
		}

		public List<ProviderConnection> listByOAuthAccount(long oauthAccountId)
		{
			var list = new List<ProviderConnection>();
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@a", oauthAccountId } };
			try
			{
				var ds = oDA.execQuery(
					"select * from provider_connection where oauthAccountID = @a order by connectionID", "DATA", "DATA", p);
				foreach (DataRow r in ds.Tables[0].Rows) list.Add(dataRowToObject(r));
			}
			catch (Exception ex) { Common.writeToLog("ERROR ProviderConnection.listByOAuthAccount:", ex); }
			return list;
		}

		// Idempotent create (unique on provider+principal+calendar). Returns the id
		// (existing or new).
		public long ensure()
		{
			var oDA = new DataAccess();
			string sql =
				"insert into provider_connection (userID, customerID, provider, displayName, principalEmail, calendarId, " +
				"m365TenantId, m365ClientId, credentialRef, authKind, oauthAccountID, enabled) " +
				"values (@user, @cust, @provider, @displayName, @principalEmail, @calendarId, @tenant, @client, @cred, @auth, @oauth, @enabled) " +
				"on duplicate key update displayName=@displayName2, m365TenantId=@tenant2, m365ClientId=@client2, " +
				"credentialRef=@cred2, authKind=@auth2, oauthAccountID=@oauth2, enabled=@enabled2";
			var p = new Dictionary<string, object>
			{
				{ "@user", userID > 0 ? userID : DBNull.Value }, { "@cust", customerID > 0 ? customerID : DBNull.Value },
				{ "@provider", provider }, { "@displayName", displayName }, { "@principalEmail", principalEmail },
				{ "@calendarId", calendarId }, { "@tenant", m365TenantId }, { "@client", m365ClientId },
				{ "@cred", credentialRef }, { "@auth", authKind },
				{ "@oauth", oauthAccountID > 0 ? oauthAccountID : DBNull.Value }, { "@enabled", enabled ? 1 : 0 },
				{ "@displayName2", displayName }, { "@tenant2", m365TenantId }, { "@client2", m365ClientId },
				{ "@cred2", credentialRef }, { "@auth2", authKind },
				{ "@oauth2", oauthAccountID > 0 ? oauthAccountID : DBNull.Value }, { "@enabled2", enabled ? 1 : 0 }
			};
			try
			{
				long id = oDA.insertData(sql, p);
				if (id > 0) { connectionID = id; return id; }
				// ON DUPLICATE with no change returns 0 — look the existing row up.
				var q = new Dictionary<string, object> { { "@pr", provider }, { "@pe", principalEmail }, { "@ci", calendarId } };
				object? existing = oDA.execScalar("select connectionID from provider_connection where provider=@pr and principalEmail=@pe and calendarId=@ci limit 1", q);
				connectionID = Common.ToLong(existing);
			}
			catch (Exception ex) { Common.writeToLog("ERROR ProviderConnection.ensure:", ex); }
			return connectionID;
		}

		// How many pairs reference this connection (either side). Callers must check this
		// is 0 before delete() — the sync_pair→provider_connection FKs are RESTRICT.
		public int countReferencingPairs(long connectionId)
		{
			var oDA = new DataAccess();
			object? v = oDA.execScalar(
				"select count(*) from sync_pair where leftConnectionID=@id or rightConnectionID=@id",
				new Dictionary<string, object> { { "@id", connectionId } });
			return Common.ToInt(v);
		}

		// Delete a connection. Caller must ensure countReferencingPairs(id)==0 first.
		public bool delete(long id)
		{
			var oDA = new DataAccess();
			return oDA.deleteData("delete from provider_connection where connectionID = @id",
				new Dictionary<string, object> { { "@id", id } }) > 0;
		}

		private ProviderConnection dataRowToObject(DataRow r) => new()
		{
			connectionID = Common.ToLong(r["connectionID"]),
			userID = Common.ToLong(r["userID"]),
			customerID = Common.ToLong(r["customerID"]),
			provider = Common.ToStr(r["provider"]),
			displayName = Common.ToStr(r["displayName"]),
			principalEmail = Common.ToStr(r["principalEmail"]),
			calendarId = Common.ToStr(r["calendarId"]),
			m365TenantId = Common.ToStr(r["m365TenantId"]),
			m365ClientId = Common.ToStr(r["m365ClientId"]),
			credentialRef = Common.ToStr(r["credentialRef"]),
			authKind = Common.ToStr(r["authKind"]),
			oauthAccountID = Common.ToLong(r["oauthAccountID"]),
			enabled = Common.ToBool(r["enabled"])
		};
	}
}
