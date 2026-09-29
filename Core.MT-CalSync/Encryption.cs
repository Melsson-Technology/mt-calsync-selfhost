using System.Security.Cryptography;
using System.Text;

namespace Core.MTCalSync
{
	// Encryption for secrets at rest (settings table, OAuth tokens): AES-256-GCM with the
	// deployment's DataEncryptionKey from settings.xml (base64 of 32 random bytes). Each
	// value gets a fresh 96-bit nonce, and GCM makes tampering fail the decrypt.
	// Format: "v2:" + Base64(nonce[12] || tag[16] || ciphertext).
	// Values without the "v2:" prefix are pre-v2 ciphertext; Decrypt() rejects them and
	// the secret must be re-entered.
	public class Encryption
	{
		private const string V2Prefix = "v2:";
		private const int NonceSize = 12;
		private const int TagSize = 16;

		public static string Encrypt(string plainText)
		{
			byte[] key = RequireKey();
			byte[] plain = Encoding.UTF8.GetBytes(plainText ?? string.Empty);
			byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
			byte[] cipher = new byte[plain.Length];
			byte[] tag = new byte[TagSize];

			using var gcm = new AesGcm(key, TagSize);
			gcm.Encrypt(nonce, plain, cipher, tag);

			byte[] packed = new byte[NonceSize + TagSize + cipher.Length];
			Buffer.BlockCopy(nonce, 0, packed, 0, NonceSize);
			Buffer.BlockCopy(tag, 0, packed, NonceSize, TagSize);
			Buffer.BlockCopy(cipher, 0, packed, NonceSize + TagSize, cipher.Length);
			return V2Prefix + Convert.ToBase64String(packed);
		}

		public static string Decrypt(string encryptedText)
		{
			if (string.IsNullOrEmpty(encryptedText)) return string.Empty;
			if (!encryptedText.StartsWith(V2Prefix, StringComparison.Ordinal))
				throw new CryptographicException(
					"Value is not in the current v2 (AES-GCM) format. Pre-v2 ciphertext is no longer " +
					"supported — re-enter the secret so it is stored under DataEncryptionKey.");

			byte[] key = RequireKey();
			byte[] packed = Convert.FromBase64String(encryptedText.Substring(V2Prefix.Length));
			if (packed.Length < NonceSize + TagSize)
				throw new CryptographicException("Ciphertext too short.");

			byte[] nonce = new byte[NonceSize];
			byte[] tag = new byte[TagSize];
			byte[] cipher = new byte[packed.Length - NonceSize - TagSize];
			Buffer.BlockCopy(packed, 0, nonce, 0, NonceSize);
			Buffer.BlockCopy(packed, NonceSize, tag, 0, TagSize);
			Buffer.BlockCopy(packed, NonceSize + TagSize, cipher, 0, cipher.Length);

			byte[] plain = new byte[cipher.Length];
			using var gcm = new AesGcm(key, TagSize);
			gcm.Decrypt(nonce, cipher, tag, plain);
			return Encoding.UTF8.GetString(plain);
		}

		// True for a pre-v2 value, which can't be decrypted and must be re-entered.
		public static bool IsLegacy(string encryptedText) =>
			!string.IsNullOrEmpty(encryptedText) && !encryptedText.StartsWith(V2Prefix, StringComparison.Ordinal);

		private static byte[] RequireKey()
		{
			string b64 = Settings.DataEncryptionKey;
			if (string.IsNullOrWhiteSpace(b64))
				throw new InvalidOperationException(
					"DataEncryptionKey is not set in settings.xml. Generate one with `openssl rand -base64 32`.");
			byte[] key;
			try { key = Convert.FromBase64String(b64); }
			catch (FormatException) { throw new InvalidOperationException("DataEncryptionKey is not valid base64."); }
			if (key.Length != 32)
				throw new InvalidOperationException("DataEncryptionKey must decode to exactly 32 bytes (AES-256).");
			return key;
		}
	}
}
