using System.Data;
using System.Reflection;
using System.Xml;

namespace Core.MTCalSync
{
	// Configuration + the DB-backed `settings` entity. Non-secret config is read
	// from settings.xml (process-cached). Secrets resolve DB-first (encrypted in
	// the settings table) with an XML plaintext fallback, so a `set-secret` CLI
	// edit takes effect without a redeploy.
	public class Settings : @base
	{
		private static readonly Lazy<Dictionary<string, string>> _cachedSettings = new(() => LoadAllSettings());

		private static Dictionary<string, string> LoadAllSettings()
		{
			var settings = new Dictionary<string, string>();
			var oXML = new XmlDocument();

			string? asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
			string candidatePath = !string.IsNullOrEmpty(asmDir)
				? Path.Combine(asmDir, "settings.xml")
				: Path.Combine(AppContext.BaseDirectory, "settings.xml");
			if (!File.Exists(candidatePath))
				candidatePath = Path.Combine(AppContext.BaseDirectory, "settings.xml");

			try { oXML.Load(candidatePath); }
			catch (Exception ex) { Common.writeToLog("WARN: could not load settings.xml (" + candidatePath + ")", ex); return settings; }

			string[] keys = {
				// Database + crypto bootstrap (settings.xml only)
				"MySqlDatabaseConnection", "DataEncryptionKey", "DataProtectionKeysPath",
				// Legacy single-admin portal login (superseded by the user table)
				"PortalAdminUser", "PortalAdminPassword",
			// Self-host operator login (single hashed password; hosted uses the user table)
			"SelfHostAdminPasswordHash",
				// Microsoft 365 / Graph (app-only client credentials)
				"GraphTenantId", "GraphClientId", "GraphClientSecret",
				// Google Workspace (service account + domain-wide delegation)
				"GoogleServiceAccountJsonPath", "GoogleServiceAccountJson",
				// Delegated OAuth app clients (user connect flows)
				"MsOAuthClientId", "MsOAuthClientSecret",
				"GoogleOAuthClientId", "GoogleOAuthClientSecret", "UnverifiedAppNotice",
				// Sync defaults (per-pair values in sync_pair override these)
				"WindowDays", "LookbackDays", "FidelityMode", "CopyAttendeesToBody",
				"MaxWritesPerRun", "FullResyncHour", "MaxDeltaPages",
				// Worker / billing (the plan's own numbers are constants, not settings — see Plan.cs)
				"PublicBaseUrl", "WorkerMaxConcurrency",
				"StripePublishableKey", "StripeSecretKey", "AbandonedTrialRetentionDays",
				// Provenance stamp namespace + this app's instance id
				"ExtPropNamespaceGuid", "AppInstanceId",
				// SMTP (failure alerts)
				"SmtpHost", "SmtpPort", "SmtpUseSsl", "SmtpUser", "SmtpPassword",
				"SmtpFrom", "AlertTo"
			};
			foreach (var key in keys)
			{
				var node = oXML.SelectSingleNode("//" + key);
				if (node != null) settings[key] = node.InnerText;
			}
			return settings;
		}

		private static string GetCachedSetting(string key) =>
			_cachedSettings.Value.TryGetValue(key, out var value) ? value : string.Empty;

		// ─── Database (settings.xml ONLY — bootstrap; the Portal needs it to reach
		//     the DB where every other setting lives) ────────────────────────
		public static string MySqlDatabaseConnection => GetCachedSetting("MySqlDatabaseConnection");

		// ─── Crypto bootstrap (settings.xml ONLY — encrypts the DB values, so it
		//     can never live in the DB itself) ───────────────────────────────
		// Base64 of 32 random bytes; provision generates it (`openssl rand -base64 32`).
		public static string DataEncryptionKey => GetCachedSetting("DataEncryptionKey");

		// Where ASP.NET DataProtection persists its key ring (cookie + state crypto).
		// Must survive deploys — the publish dir is swapped atomically, so default to
		// /etc/mtcalsync/dpkeys when that config root exists (server), else a stable
		// local folder for dev.
		public static string DataProtectionKeysPath
		{
			get
			{
				string configured = GetCachedSetting("DataProtectionKeysPath");
				if (!string.IsNullOrWhiteSpace(configured)) return configured;
				return Directory.Exists("/etc/mtcalsync")
					? "/etc/mtcalsync/dpkeys"
					: Path.Combine(AppContext.BaseDirectory, "dpkeys");
			}
		}

		// ─── Legacy single-admin portal login (pre-user-table; kept only so the
		//     break-glass CLI docs stay accurate) ────────────────────────────
		// Single PBKDF2 password hash for the self-host portal's one operator (set with
		// the `set-admin-password` worker command). The hosted portal uses the user
		// table instead; this key is read only by the self-host portal.
		public static string SelfHostAdminPasswordHash => resolveRaw("SelfHostAdminPasswordHash");

		public static string PortalAdminUser => GetCachedSetting("PortalAdminUser");
		public static string PortalAdminPassword => GetCachedSetting("PortalAdminPassword");

		// Web-editable config below resolves DB-first (the Portal writes it there) with
		// an XML fallback, so the admin UI is the source of truth without redeploying.

		// ─── Microsoft Graph ────────────────────────────────────────────────
		public static string GraphTenantId => resolveRaw("GraphTenantId");
		public static string GraphClientId => resolveRaw("GraphClientId");
		public static string EffectiveGraphClientSecret => resolveKey("GraphClientSecret", GetCachedSetting("GraphClientSecret"));

		// ─── Delegated OAuth app clients ────────────────────────────────────
		// The multitenant Entra app + Google OAuth web client that users consent to.
		// Distinct from the app-only Graph registration / service account above,
		// which stay dedicated to operator (app_default) connections.
		public static string MsOAuthClientId => resolveRaw("MsOAuthClientId");
		public static string EffectiveMsOAuthClientSecret => resolveKey("MsOAuthClientSecret", GetCachedSetting("MsOAuthClientSecret"));
		public static string GoogleOAuthClientId => resolveRaw("GoogleOAuthClientId");
		public static string EffectiveGoogleOAuthClientSecret => resolveKey("GoogleOAuthClientSecret", GetCachedSetting("GoogleOAuthClientSecret"));
		// Show the "you may see an unverified-app screen" note on the connect page.
		// On until the provider verifications clear (also accurate for self-hosters
		// running their own unverified OAuth clients).
		public static bool UnverifiedAppNotice => parseBool(resolveRaw("UnverifiedAppNotice"), true);

		// ─── Google ─────────────────────────────────────────────────────────
		// A path to the SA JSON key file (chmod 0600) OR inline JSON stored encrypted
		// in the DB (paste it in the Portal). Path wins when set.
		public static string GoogleServiceAccountJsonPath => resolveRaw("GoogleServiceAccountJsonPath");
		public static string EffectiveGoogleServiceAccountJson
		{
			get
			{
				string path = GoogleServiceAccountJsonPath;
				if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
				{
					try { return File.ReadAllText(path); }
					catch (Exception ex) { Common.writeToLog("ERROR reading GoogleServiceAccountJsonPath " + path, ex); }
				}
				return resolveKey("GoogleServiceAccountJson", GetCachedSetting("GoogleServiceAccountJson"));
			}
		}

		// ─── Sync defaults ──────────────────────────────────────────────────
		public static int WindowDays => (int)parseDecimal(resolveRaw("WindowDays"), 60m);
		public static int LookbackDays => (int)parseDecimal(resolveRaw("LookbackDays"), 1m);
		public static string FidelityMode => withDefault(resolveRaw("FidelityMode"), "full_detail");
		public static bool CopyAttendeesToBody => parseBool(resolveRaw("CopyAttendeesToBody"), false);
		public static int MaxWritesPerRun => (int)parseDecimal(resolveRaw("MaxWritesPerRun"), 25m);
		public static int FullResyncHour => (int)parseDecimal(resolveRaw("FullResyncHour"), 3m);
		public static int MaxDeltaPages => (int)parseDecimal(resolveRaw("MaxDeltaPages"), 50m);

		// ─── Worker ─────────────────────────────────────────────────────────
		// Trial length, plan price and the calendar allowance are deliberately NOT
		// settings. They are compile-time facts in the SaaS layer's Plan.cs, because
		// changing one also changes marketing prose that no settings row can reach.
		// Concurrent pair runs per worker tick — sized to the box, not the tenant count.
		public static int WorkerMaxConcurrency => (int)parseDecimal(resolveRaw("WorkerMaxConcurrency"), 2m);

		// ─── Billing (Stripe) ───────────────────────────────────────────────
		public static string StripePublishableKey => resolveRaw("StripePublishableKey");
		public static string EffectiveStripeSecretKey => resolveKey("StripeSecretKey", GetCachedSetting("StripeSecretKey"));
		// Days after an unconverted trial's end before stored OAuth tokens are
		// revoked/deleted (holding live credentials for abandoned trials is pure risk).
		public static int AbandonedTrialRetentionDays => (int)parseDecimal(resolveRaw("AbandonedTrialRetentionDays"), 30m);
		// Absolute origin for links in outbound email (reset links etc.), no trailing slash.
		public static string PublicBaseUrl => resolveRaw("PublicBaseUrl").TrimEnd('/');

		// Fixed GUID for the Graph singleValueExtendedProperties namespace + Google
		// private extended-property prefix. Keep this STABLE across deploys — changing
		// it orphans every existing provenance stamp.
		public static string ExtPropNamespaceGuid =>
			withDefault(GetCachedSetting("ExtPropNamespaceGuid"), "b7c9e3a2-6d41-4f8b-9c2e-a1b2c3d4e5f6");
		public static string AppInstanceId => withDefault(GetCachedSetting("AppInstanceId"), "mt-calsync");

		// ─── Branding (email + public copy) ─────────────────────────────────
		// All overridable via the settings table so a self-hoster rebrands the
		// whole product without touching code. Defaults derive from each other
		// (signature/support follow AppName / SmtpFrom), so setting AppName alone
		// carries most of the way. CompanyName/Address feed the CAN-SPAM footer.
		public static string AppName => withDefault(resolveRaw("AppName"), "MT-CalSync");
		public static string SupportEmail => withDefault(resolveRaw("SupportEmail"), SmtpFrom);
		public static string EmailSignature => withDefault(resolveRaw("EmailSignature"), "The " + AppName + " team");
		public static string CompanyName => resolveRaw("CompanyName");        // blank ok
		public static string CompanyAddress => resolveRaw("CompanyAddress");  // blank until public launch

		// ─── SMTP ───────────────────────────────────────────────────────────
		public static string SmtpHost => resolveRaw("SmtpHost");
		public static int SmtpPort => (int)parseDecimal(resolveRaw("SmtpPort"), 587m);
		public static bool SmtpUseSsl => parseBool(resolveRaw("SmtpUseSsl"), true);
		public static string SmtpUser => resolveRaw("SmtpUser");
		public static string EffectiveSmtpPassword => resolveKey("SmtpPassword", GetCachedSetting("SmtpPassword"));
		public static string SmtpFrom => resolveRaw("SmtpFrom");
		public static string AlertTo => resolveRaw("AlertTo");

		// ─── helpers ────────────────────────────────────────────────────────
		private static string withDefault(string val, string def) => string.IsNullOrWhiteSpace(val) ? def : val;

		private static decimal parseDecimal(string val, decimal fallback) =>
			string.IsNullOrWhiteSpace(val) ? fallback : (decimal.TryParse(val, out var d) ? d : fallback);

		private static bool parseBool(string val, bool fallback)
		{
			if (string.IsNullOrWhiteSpace(val)) return fallback;
			return val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1"
				|| val.Equals("yes", StringComparison.OrdinalIgnoreCase);
		}

		// Live single-value read from the DB settings table (bypasses the XML cache).
		private static string getDbSetting(string name)
		{
			try
			{
				DataAccess oDA = new DataAccess();
				string sql = "select settingValue from settings where settingName = @n limit 1";
				var p = new Dictionary<string, object> { { "@n", name } };
				object? val = oDA.execScalar(sql, p);
				return val?.ToString() ?? string.Empty;
			}
			catch (Exception ex)
			{
				Common.writeToLog("ERROR in getDbSetting(" + name + "):", ex);
				return string.Empty;
			}
		}

		// DB (plaintext) → XML fallback, for non-secret web-editable config. The Portal
		// writes these into the settings table, so a UI edit takes effect immediately.
		private static string resolveRaw(string name)
		{
			string db = getDbSetting(name);
			return string.IsNullOrWhiteSpace(db) ? GetCachedSetting(name) : db;
		}

		// DB (encrypted) → XML fallback. A decrypt failure falls back to the file value.
		private static string resolveKey(string dbName, string xmlFallback)
		{
			string enc = getDbSetting(dbName);
			if (!string.IsNullOrWhiteSpace(enc))
			{
				try { return Encryption.Decrypt(enc); }
				catch (Exception ex) { Common.writeToLog("ERROR decrypting setting " + dbName + " — using file fallback:", ex); }
			}
			return xmlFallback;
		}

		// ─── DB-backed settings entity ──────────────────────────────────────
		public long settingID { get; set; }
		public string settingName { get; set; } = string.Empty;
		public string settingValue { get; set; } = string.Empty;

		public Settings getByName(string name)
		{
			Settings oSet = new Settings();
			DataAccess oDA = new DataAccess();
			string sqlString = "select * from settings where settingName = @n limit 1";
			var parameters = new Dictionary<string, object> { { "@n", name ?? string.Empty } };
			try
			{
				DataSet tmpDS = oDA.execQuery(sqlString, "DATA", "DATA", parameters);
				errorMessage = oDA.errorMessage;
				if (tmpDS.Tables[0].Rows.Count > 0)
					oSet = dataRowToObject(tmpDS.Tables[0].Rows[0]);
			}
			catch (Exception ex)
			{
				errorMessage = ex.Message;
				Common.writeToLog("ERROR in getByName():", ex);
			}
			return oSet;
		}

		// Upsert by name (settingName is UNIQUE).
		public long saveByName(string name, string value)
		{
			long rtn = 0;
			DataAccess oDA = new DataAccess();
			string sqlString =
				"insert into settings (settingName, settingValue) values (@n, @v) " +
				"on duplicate key update settingValue = @v2, dateLastModified = NOW()";
			var parameters = new Dictionary<string, object>
			{
				{ "@n", name ?? string.Empty },
				{ "@v", value ?? string.Empty },
				{ "@v2", value ?? string.Empty }
			};
			try
			{
				rtn = oDA.insertData(sqlString, parameters);
				errorMessage = oDA.errorMessage;
			}
			catch (Exception ex)
			{
				errorMessage = ex.Message;
				Common.writeToLog("ERROR in saveByName():", ex);
			}
			return rtn;
		}

		private Settings dataRowToObject(DataRow dRow)
		{
			return new Settings()
			{
				settingID = long.Parse(dRow["settingID"]?.ToString() ?? "0"),
				settingName = dRow["settingName"]?.ToString() ?? string.Empty,
				settingValue = dRow["settingValue"]?.ToString() ?? string.Empty
			};
		}
	}
}
