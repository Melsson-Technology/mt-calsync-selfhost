namespace Core.MTCalSync
{
	// Builds the ICalendarProvider for a connection; the one place credentials are
	// resolved. connection.authKind selects the path:
	//   app_default     - app credentials (Graph client secret, or Google service account).
	//   delegated_oauth - the owner's OAuth grant. A flagged grant throws
	//                     NeedsReauthException before any network call.
	public static class ProviderFactory
	{
		// Tests only: lets a test run the engine against in-memory calendars.
		public static Func<ProviderConnection, bool, ICalendarProvider>? TestOverride { get; set; }

		public static ICalendarProvider Create(ProviderConnection conn, bool seriesMode = false)
		{
			if (TestOverride != null) return TestOverride(conn, seriesMode);

			if (conn.authKind == AuthKinds.DelegatedOauth)
				return CreateDelegated(conn, seriesMode);

			if (conn.provider == Providers.M365)
			{
				string tenant = !string.IsNullOrWhiteSpace(conn.m365TenantId) ? conn.m365TenantId : Settings.GraphTenantId;
				string client = !string.IsNullOrWhiteSpace(conn.m365ClientId) ? conn.m365ClientId : Settings.GraphClientId;
				string secret = Settings.EffectiveGraphClientSecret;
				if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(client) || string.IsNullOrWhiteSpace(secret))
					throw new InvalidOperationException("Graph credentials missing (GraphTenantId/GraphClientId/GraphClientSecret).");
				return new GraphCalendarProvider(tenant, client, secret, conn.principalEmail, conn.calendarId, seriesMode);
			}

			if (conn.provider == Providers.Google)
			{
				string saJson = Settings.EffectiveGoogleServiceAccountJson;
				if (string.IsNullOrWhiteSpace(saJson))
					throw new InvalidOperationException("Google service-account JSON missing (GoogleServiceAccountJsonPath or GoogleServiceAccountJson).");
				return new GoogleCalendarProvider(saJson, conn.principalEmail, conn.calendarId, seriesMode);
			}

			throw new InvalidOperationException($"Unknown provider '{conn.provider}'.");
		}

		private static ICalendarProvider CreateDelegated(ProviderConnection conn, bool seriesMode)
		{
			var account = new OAuthAccount().getById(conn.oauthAccountID);
			if (account.oauthAccountID == 0)
				throw new NeedsReauthException(conn.oauthAccountID, conn.provider, $"Connection {conn.connectionID} references a missing OAuth account.");
			if (!account.isConnected)
				throw new NeedsReauthException(account.oauthAccountID, conn.provider, $"OAuth account {account.oauthAccountID} is {account.status}.");

			if (conn.provider == Providers.M365)
			{
				// Address the mailbox by the immutable Entra object id; a delegated
				// token treats /users/{own-oid} like /me.
				var cred = new MsDelegatedTokenCredential(account.oauthAccountID);
				return new GraphCalendarProvider(cred, account.providerAccountId, conn.calendarId, seriesMode);
			}

			if (conn.provider == Providers.Google)
			{
				var cred = GoogleOAuthFlow.BuildUserCredential(account);
				return new GoogleCalendarProvider(cred, account.oauthAccountID, conn.calendarId, seriesMode);
			}

			throw new InvalidOperationException($"Unknown provider '{conn.provider}'.");
		}
	}
}
