using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Core.MTCalSync
{
	// Shared helpers. writeToLog writes a rolling per-day file under logs/ next to
	// the assembly; systemd also captures stdout to journald. Plus the greppable
	// structured audit line the sync engine emits.
	public class Common
	{
		private static string GetExecutableDirectory()
		{
			string? exePath = Assembly.GetEntryAssembly()?.Location;
			return Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
		}

		private static readonly object _logLock = new();

		public static void writeToLog(string logText)
		{
			try
			{
				string exeDirectory = GetExecutableDirectory();
				string logDirectory = Path.Combine(exeDirectory, "logs");
				if (!Directory.Exists(logDirectory))
					Directory.CreateDirectory(logDirectory);

				string fileName = $"appLog{DateTime.Now.Year}-{DateTime.Now.Month:D2}-{DateTime.Now.Day:D2}.txt";
				string filePath = Path.Combine(logDirectory, fileName);

				lock (_logLock)
				{
					using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
					using StreamWriter writer = new StreamWriter(stream);
					writer.WriteLine("/----------------------------------------------------------------------------------- ");
					writer.WriteLine(" Date: " + DateTime.Now.ToString());
					writer.WriteLine(" Info: " + logText);
					writer.WriteLine("-----------------------------------------------------------------------------------/" + Environment.NewLine);
				}
			}
			catch { /* logging must never throw */ }
		}

		public static void writeToLog(string logText, Exception ex) => writeToLog(logText + ex.ToString());

		// Structured, grep-friendly one-liner for per-item sync decisions. Also echoed
		// to stdout so journalctl shows it. Example:
		//   MTCS pair=42 run=1001 side=google op=create unit=occurrence origin=m365 mirror=abc hash=9f3a result=ok
		public static void audit(string line)
		{
			Console.WriteLine("MTCS " + line);
			writeToLog("MTCS " + line);
		}

		public static string getMySqlNow() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

		public static string makeMySqlDate(DateTime inDate)
		{
			if (inDate == default) return "1970-01-01 00:00:00";
			return inDate.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");
		}

		// Google/RFC3339 UTC timestamp ("...Z").
		public static string ConvertDateTimeToRfc3339(DateTime dateTime)
		{
			if (dateTime.Kind != DateTimeKind.Utc)
				dateTime = dateTime.ToUniversalTime();
			return dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
		}

		public static bool IsNumeric(string input, NumberStyles numberStyle) =>
			double.TryParse(input, numberStyle, CultureInfo.CurrentCulture, out _);

		// ── DataRow coercion helpers (used by every dataRowToObject) ──────────
		// All DATETIME columns are written as UTC; reads come back Kind=Unspecified,
		// so ToDateTimeUtc re-stamps them Utc.
		public static string ToStr(object? v) => (v == null || v == DBNull.Value) ? string.Empty : (v.ToString() ?? string.Empty);
		public static string? ToStrOrNull(object? v) => (v == null || v == DBNull.Value) ? null : v.ToString();
		public static long ToLong(object? v) => (v == null || v == DBNull.Value) ? 0 : (long.TryParse(v.ToString(), out var l) ? l : 0);
		public static int ToInt(object? v) => (v == null || v == DBNull.Value) ? 0 : (int.TryParse(v.ToString(), out var i) ? i : 0);
		public static bool ToBool(object? v)
		{
			if (v == null || v == DBNull.Value) return false;
			if (v is bool b) return b;
			string s = v.ToString() ?? string.Empty;
			return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
		}
		public static DateTime? ToDateTimeUtc(object? v)
		{
			if (v == null || v == DBNull.Value) return null;
			if (v is DateTime dt) return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
			return DateTime.TryParse(v.ToString(), out var p) ? DateTime.SpecifyKind(p, DateTimeKind.Utc) : null;
		}

		// SHA-256 hex of a UTF-8 string. Used for (a) the fixed 64-char event-key that
		// indexes long provider ids, and (b) the content hash that drives change/echo
		// detection.
		public static string Sha256Hex(string? input)
		{
			byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input ?? string.Empty));
			var sb = new StringBuilder(bytes.Length * 2);
			foreach (byte b in bytes) sb.Append(b.ToString("x2"));
			return sb.ToString();
		}
	}
}
