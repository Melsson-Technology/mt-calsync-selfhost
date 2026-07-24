using System.Security.Cryptography;

namespace Core.MTCalSync
{
	// One-way password hashing — PBKDF2-SHA256, per-user random salt, fixed-time
	// verify. Stored format: "{iterations}.{saltBase64}.{hashBase64}", so the cost
	// can be raised later and old hashes keep verifying (and can be re-hashed on
	// the next successful login if their iteration count is below current).
	// Lives in Core (not the web layer) because the worker CLI also sets passwords.
	public static class PasswordHasher
	{
		private const int Iterations = 100_000;
		private const int SaltSize = 16;
		private const int HashSize = 32;

		public static string Hash(string password)
		{
			byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
			byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password ?? string.Empty, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
			return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
		}

		public static bool Verify(string password, string stored)
		{
			if (string.IsNullOrWhiteSpace(stored)) return false;
			var parts = stored.Split('.', 3);
			if (parts.Length != 3) return false;
			if (!int.TryParse(parts[0], out int iterations) || iterations < 1) return false;

			byte[] salt, expected;
			try
			{
				salt = Convert.FromBase64String(parts[1]);
				expected = Convert.FromBase64String(parts[2]);
			}
			catch (FormatException) { return false; }

			byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password ?? string.Empty, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
			return CryptographicOperations.FixedTimeEquals(actual, expected);
		}

		// Minimal strength gate for signup/reset. Returns an empty string when the
		// password is acceptable, else a user-facing reason.
		public static string CheckStrength(string password)
		{
			if (string.IsNullOrEmpty(password) || password.Length < 8)
				return "Password must be at least 8 characters.";
			if (password.Length > 256)
				return "Password is too long (max 256 characters).";
			return string.Empty;
		}
	}
}
