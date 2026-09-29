using System.Data;
using System.Reflection;
using System.Xml;

namespace Core.MTCalSync
{
	// Configuration, plus the `settings` table entity. settings.xml is read once per
	// process. Most values resolve from the settings table first (secrets encrypted), then
	// settings.xml, so a portal or `set-secret` change applies without a restart.
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

			// Every element is read, so an application built on the engine can keep its own
			// keys in the same file and read them through Resolve and ResolveSecret.
			foreach (XmlNode node in oXML.SelectNodes("//*[not(*)]")!)
				settings[node.Name] = node.InnerText;
			return settings;
		}

		private static string GetCachedSetting(string key) =>
			_cachedSettings.Value.TryGetValue(key, out var value) ? value : string.Empty;

		// Database: settings.xml only, since it is needed to reach the settings table.
		public static string MySqlDatabaseConnection => GetCachedSetting("MySqlDatabaseConnection");

		// Encryption key: settings.xml only, since it encrypts the table's secrets.
		// Base64 of 32 random bytes (`openssl rand -base64 32`).
		public static string DataEncryptionKey => GetCachedSetting("DataEncryptionKey");

		// Where ASP.NET DataProtection keeps its key ring. It must outlive the publish
		// directory, which is replaced on upgrade, so on a server it defaults to
		// /etc/mtcalsync/dpkeys, and elsewhere to a local folder.
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

		// Portal operator login: the PBKDF2 hash set by `set-admin-password`.
		public static string SelfHostAdminPasswordHash => resolveRaw("SelfHostAdminPasswordHash");

		// Everything below is editable in the portal, which writes it to the settings table.

		// Microsoft Graph
		public static string GraphTenantId => resolveRaw("GraphTenantId");
		public static string GraphClientId => resolveRaw("GraphClientId");
		public static string EffectiveGraphClientSecret => resolveKey("GraphClientSecret", GetCachedSetting("GraphClientSecret"));

		// Delegated OAuth clients
		// The multitenant Entra app and Google OAuth web client that users consent to.
		// The app-only Graph registration and service account serve app_default connections.
		public static string MsOAuthClientId => resolveRaw("MsOAuthClientId");
		public static string EffectiveMsOAuthClientSecret => resolveKey("MsOAuthClientSecret", GetCachedSetting("MsOAuthClientSecret"));
		public static string GoogleOAuthClientId => resolveRaw("GoogleOAuthClientId");
		public static string EffectiveGoogleOAuthClientSecret => resolveKey("GoogleOAuthClientSecret", GetCachedSetting("GoogleOAuthClientSecret"));
		// Shows the "you may see an unverified-app screen" note on the connect page. On by
		// default, since OAuth clients are usually unverified.
		public static bool UnverifiedAppNotice => parseBool(resolveRaw("UnverifiedAppNotice"), true);

		// Google
		// The service account key: a path to the JSON file (chmod 0600), or the JSON stored
		// encrypted in the settings table. The path wins when set.
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

		// Sync defaults
		public static int WindowDays => (int)parseDecimal(resolveRaw("WindowDays"), 60m);
		public static int LookbackDays => (int)parseDecimal(resolveRaw("LookbackDays"), 1m);
		public static string FidelityMode => withDefault(resolveRaw("FidelityMode"), "full_detail");
		public static bool CopyAttendeesToBody => parseBool(resolveRaw("CopyAttendeesToBody"), false);
		public static int MaxWritesPerRun => (int)parseDecimal(resolveRaw("MaxWritesPerRun"), 25m);
		public static int FullResyncHour => (int)parseDecimal(resolveRaw("FullResyncHour"), 3m);
		public static int MaxDeltaPages => (int)parseDecimal(resolveRaw("MaxDeltaPages"), 50m);

		// Worker
		// Pairs run at once per worker tick; size it to the machine.
		public static int WorkerMaxConcurrency => (int)parseDecimal(resolveRaw("WorkerMaxConcurrency"), 2m);

		// The portal's public origin, no trailing slash, used for OAuth redirect URIs.
		// Empty means the request's own host.
		public static string PublicBaseUrl => resolveRaw("PublicBaseUrl").TrimEnd('/');

		// Namespace GUID for the Graph extended properties and the Google private-property
		// prefix. Never change it: every existing provenance stamp would be orphaned.
		public static string ExtPropNamespaceGuid =>
			withDefault(GetCachedSetting("ExtPropNamespaceGuid"), "b7c9e3a2-6d41-4f8b-9c2e-a1b2c3d4e5f6");
		public static string AppInstanceId => withDefault(GetCachedSetting("AppInstanceId"), "mt-calsync");

		// Branding
		// The product name in alert emails.
		public static string AppName => withDefault(resolveRaw("AppName"), "MT-CalSync");

		// SMTP
		public static string SmtpHost => resolveRaw("SmtpHost");
		public static int SmtpPort => (int)parseDecimal(resolveRaw("SmtpPort"), 587m);
		public static bool SmtpUseSsl => parseBool(resolveRaw("SmtpUseSsl"), true);
		public static string SmtpUser => resolveRaw("SmtpUser");
		public static string EffectiveSmtpPassword => resolveKey("SmtpPassword", GetCachedSetting("SmtpPassword"));
		public static string SmtpFrom => resolveRaw("SmtpFrom");
		public static string AlertTo => resolveRaw("AlertTo");

		// Helpers
		private static string withDefault(string val, string def) => string.IsNullOrWhiteSpace(val) ? def : val;

		private static decimal parseDecimal(string val, decimal fallback) =>
			string.IsNullOrWhiteSpace(val) ? fallback : (decimal.TryParse(val, out var d) ? d : fallback);

		private static bool parseBool(string val, bool fallback)
		{
			if (string.IsNullOrWhiteSpace(val)) return fallback;
			return val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1"
				|| val.Equals("yes", StringComparison.OrdinalIgnoreCase);
		}

		// Reads one value from the settings table on every call; nothing is cached.
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

		// For settings the engine itself doesn't define: a plain value, and a secret stored
		// encrypted. Both read the database first, then settings.xml.
		public static string Resolve(string name) => resolveRaw(name);
		public static string ResolveSecret(string name) => resolveKey(name, GetCachedSetting(name));

		// Plain value: the settings table, then settings.xml.
		private static string resolveRaw(string name)
		{
			string db = getDbSetting(name);
			return string.IsNullOrWhiteSpace(db) ? GetCachedSetting(name) : db;
		}

		// Secret: the encrypted table value, then settings.xml. A value that won't decrypt
		// is logged and the file value used.
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

		// The settings table entity
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

		// Upsert by name; settingName is unique.
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
