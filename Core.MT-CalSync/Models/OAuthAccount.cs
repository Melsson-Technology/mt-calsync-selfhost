using System.Data;

namespace Core.MTCalSync
{
	// A user's OAuth grant for one external account (Microsoft or Google). Holds
	// the encrypted refresh token (the durable credential) plus a cached access
	// token so short-lived worker processes don't hit the token endpoint every
	// cycle. Multiple provider_connection rows (one per calendar) share one grant.
	public class OAuthAccount : @base
	{
		public const string StatusConnected = "connected";
		public const string StatusNeedsReauth = "needs_reauth";

		public long oauthAccountID { get; set; }
		public long userID { get; set; }
		public long customerID { get; set; }
		public string provider { get; set; } = string.Empty;          // m365|google
		public string providerAccountId { get; set; } = string.Empty; // Entra oid / Google sub
		public string tenantId { get; set; } = string.Empty;          // Entra home tenant
		public string principalEmail { get; set; } = string.Empty;
		public string displayName { get; set; } = string.Empty;
		public string scopesGranted { get; set; } = string.Empty;
		public string refreshTokenEnc { get; set; } = string.Empty;
		public string accessTokenEnc { get; set; } = string.Empty;
		public DateTime? accessTokenExpiresAt { get; set; }
		public string status { get; set; } = StatusConnected;
		public DateTime? lastRefreshAt { get; set; }
		public DateTime? lastReauthNotifyAt { get; set; }
		public DateTime? backoffUntil { get; set; }
		public string lastError { get; set; } = string.Empty;

		public bool isConnected => status == StatusConnected;

		// Decrypt-on-demand — plaintext tokens are never kept on the entity.
		public string decryptRefreshToken() => string.IsNullOrEmpty(refreshTokenEnc) ? string.Empty : Encryption.Decrypt(refreshTokenEnc);
		public string decryptAccessToken() => string.IsNullOrEmpty(accessTokenEnc) ? string.Empty : Encryption.Decrypt(accessTokenEnc);

		public OAuthAccount getById(long id)
		{
			var o = new OAuthAccount();
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@id", id } };
			try
			{
				var ds = oDA.execQuery("select * from oauth_account where oauthAccountID = @id", "DATA", "DATA", p);
				if (ds.Tables[0].Rows.Count > 0) o = dataRowToObject(ds.Tables[0].Rows[0]);
			}
			catch (Exception ex) { Common.writeToLog("ERROR OAuthAccount.getById:", ex); }
			return o;
		}

		public OAuthAccount getByProviderAccount(string providerName, string accountId)
		{
			var o = new OAuthAccount();
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@p", providerName }, { "@a", accountId } };
			try
			{
				var ds = oDA.execQuery(
					"select * from oauth_account where provider = @p and providerAccountId = @a limit 1",
					"DATA", "DATA", p);
				if (ds.Tables[0].Rows.Count > 0) o = dataRowToObject(ds.Tables[0].Rows[0]);
			}
			catch (Exception ex) { Common.writeToLog("ERROR OAuthAccount.getByProviderAccount:", ex); }
			return o;
		}

		public List<OAuthAccount> listByUser(long userId)
		{
			var list = new List<OAuthAccount>();
			var oDA = new DataAccess();
			var p = new Dictionary<string, object> { { "@u", userId } };
			try
			{
				var ds = oDA.execQuery(
					"select * from oauth_account where userID = @u order by provider, oauthAccountID", "DATA", "DATA", p);
				foreach (DataRow r in ds.Tables[0].Rows) list.Add(dataRowToObject(r));
			}
			catch (Exception ex) { Common.writeToLog("ERROR OAuthAccount.listByUser:", ex); }
			return list;
		}

		public long save()
		{
			var oDA = new DataAccess();
			try
			{
				if (oauthAccountID == 0)
				{
					string sql =
						"insert into oauth_account (userID, customerID, provider, providerAccountId, tenantId, principalEmail, " +
						"displayName, scopesGranted, refreshTokenEnc, accessTokenEnc, accessTokenExpiresAt, status, lastRefreshAt) " +
						"values (@u, @c, @p, @a, @t, @em, @dn, @sc, @rt, @at, @exp, @st, @lr)";
					oauthAccountID = oDA.insertData(sql, buildParams(includeId: false));
					errorMessage = oDA.errorMessage;
				}
				else
				{
					string sql =
						"update oauth_account set userID=@u, customerID=@c, provider=@p, providerAccountId=@a, tenantId=@t, " +
						"principalEmail=@em, displayName=@dn, scopesGranted=@sc, refreshTokenEnc=@rt, accessTokenEnc=@at, " +
						"accessTokenExpiresAt=@exp, status=@st, lastRefreshAt=@lr where oauthAccountID=@id";
					oDA.updateData(sql, buildParams(includeId: true));
					errorMessage = oDA.errorMessage;
				}
			}
			catch (Exception ex) { errorMessage = ex.Message; Common.writeToLog("ERROR OAuthAccount.save:", ex); }
			return oauthAccountID;
		}

		// Persist rotated/refreshed tokens. Null keeps the stored value (Google never
		// rotates refresh tokens; Microsoft usually returns a new one).
		public void updateTokens(long id, string? newRefreshTokenEnc, string? newAccessTokenEnc, DateTime? expiresAt)
		{
			var oDA = new DataAccess();
			var sets = new List<string> { "lastRefreshAt=@now", "status=@st", "lastError=NULL" };
			var p = new Dictionary<string, object>
			{
				{ "@now", Common.getMySqlNow() }, { "@st", StatusConnected }, { "@id", id }
			};
			if (newRefreshTokenEnc != null) { sets.Add("refreshTokenEnc=@rt"); p.Add("@rt", newRefreshTokenEnc); }
			if (newAccessTokenEnc != null)
			{
				sets.Add("accessTokenEnc=@at"); p.Add("@at", newAccessTokenEnc);
				sets.Add("accessTokenExpiresAt=@exp");
				p.Add("@exp", expiresAt.HasValue ? Common.makeMySqlDate(expiresAt.Value) : DBNull.Value);
			}
			oDA.updateData($"update oauth_account set {string.Join(", ", sets)} where oauthAccountID=@id", p);
		}

		public void markNeedsReauth(long id, string error)
		{
			var oDA = new DataAccess();
			oDA.updateData(
				"update oauth_account set status=@st, lastError=@err, accessTokenEnc=NULL, accessTokenExpiresAt=NULL " +
				"where oauthAccountID=@id",
				new Dictionary<string, object>
				{
					{ "@st", StatusNeedsReauth },
					{ "@err", (error ?? string.Empty).Length > 2000 ? error!.Substring(0, 2000) : error ?? string.Empty },
					{ "@id", id }
				});
			Common.writeToLog($"OAuthAccount {id} marked needs_reauth: {error}");
		}

		public void setBackoffUntil(long id, DateTime untilUtc)
		{
			var oDA = new DataAccess();
			oDA.updateData("update oauth_account set backoffUntil=@u where oauthAccountID=@id",
				new Dictionary<string, object> { { "@u", Common.makeMySqlDate(untilUtc) }, { "@id", id } });
		}

		public void recordReauthNotify(long id)
		{
			var oDA = new DataAccess();
			oDA.updateData("update oauth_account set lastReauthNotifyAt=@n where oauthAccountID=@id",
				new Dictionary<string, object> { { "@n", Common.getMySqlNow() }, { "@id", id } });
		}

		// How many calendar connections hang off this grant.
		public int countConnections(long id)
		{
			var oDA = new DataAccess();
			object? v = oDA.execScalar("select count(*) from provider_connection where oauthAccountID=@id",
				new Dictionary<string, object> { { "@id", id } });
			return Common.ToInt(v);
		}

		public bool delete(long id)
		{
			var oDA = new DataAccess();
			return oDA.deleteData("delete from oauth_account where oauthAccountID = @id",
				new Dictionary<string, object> { { "@id", id } }) > 0;
		}

		private Dictionary<string, object> buildParams(bool includeId)
		{
			var p = new Dictionary<string, object>
			{
				{ "@u", userID }, { "@c", customerID }, { "@p", provider }, { "@a", providerAccountId },
				{ "@t", string.IsNullOrWhiteSpace(tenantId) ? DBNull.Value : tenantId },
				{ "@em", principalEmail }, { "@dn", displayName },
				{ "@sc", string.IsNullOrWhiteSpace(scopesGranted) ? DBNull.Value : scopesGranted },
				{ "@rt", string.IsNullOrWhiteSpace(refreshTokenEnc) ? DBNull.Value : refreshTokenEnc },
				{ "@at", string.IsNullOrWhiteSpace(accessTokenEnc) ? DBNull.Value : accessTokenEnc },
				{ "@exp", accessTokenExpiresAt.HasValue ? Common.makeMySqlDate(accessTokenExpiresAt.Value) : DBNull.Value },
				{ "@st", status },
				{ "@lr", lastRefreshAt.HasValue ? Common.makeMySqlDate(lastRefreshAt.Value) : DBNull.Value }
			};
			if (includeId) p.Add("@id", oauthAccountID);
			return p;
		}

		private OAuthAccount dataRowToObject(DataRow r) => new()
		{
			oauthAccountID = Common.ToLong(r["oauthAccountID"]),
			userID = Common.ToLong(r["userID"]),
			customerID = Common.ToLong(r["customerID"]),
			provider = Common.ToStr(r["provider"]),
			providerAccountId = Common.ToStr(r["providerAccountId"]),
			tenantId = Common.ToStr(r["tenantId"]),
			principalEmail = Common.ToStr(r["principalEmail"]),
			displayName = Common.ToStr(r["displayName"]),
			scopesGranted = Common.ToStr(r["scopesGranted"]),
			refreshTokenEnc = Common.ToStr(r["refreshTokenEnc"]),
			accessTokenEnc = Common.ToStr(r["accessTokenEnc"]),
			accessTokenExpiresAt = Common.ToDateTimeUtc(r["accessTokenExpiresAt"]),
			status = Common.ToStr(r["status"]),
			lastRefreshAt = Common.ToDateTimeUtc(r["lastRefreshAt"]),
			lastReauthNotifyAt = Common.ToDateTimeUtc(r["lastReauthNotifyAt"]),
			backoffUntil = Common.ToDateTimeUtc(r["backoffUntil"]),
			lastError = Common.ToStr(r["lastError"])
		};
	}
}
