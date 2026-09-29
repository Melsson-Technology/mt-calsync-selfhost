using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Tests.MTCalSync
{
	// The engine reads its encryption key from a settings.xml beside the assembly. Tests get a
	// fresh random key, written before anything reads settings, and no database connection, so
	// no test can reach one.
	internal static class TestSettings
	{
		[ModuleInitializer]
		internal static void WriteSettingsFile()
		{
			string path = Path.Combine(AppContext.BaseDirectory, "settings.xml");
			string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
			File.WriteAllText(path, $"<settings><DataEncryptionKey>{key}</DataEncryptionKey></settings>");
		}
	}
}
