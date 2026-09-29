using System.Security.Cryptography;

namespace Tests.MTCalSync
{
	// Stored secrets (OAuth tokens, client secrets, SMTP password) and the operator password.
	public class SecretsTests
	{
		[Fact]
		public void A_secret_decrypts_to_what_was_encrypted()
		{
			string cipher = Encryption.Encrypt("refresh-token-value");
			Assert.StartsWith("v2:", cipher);
			Assert.Equal("refresh-token-value", Encryption.Decrypt(cipher));
		}

		[Fact]
		public void The_same_secret_encrypts_differently_each_time() =>
			Assert.NotEqual(Encryption.Encrypt("same"), Encryption.Encrypt("same"));

		[Fact]
		public void A_tampered_ciphertext_fails_to_decrypt()
		{
			byte[] packed = Convert.FromBase64String(Encryption.Encrypt("secret")[3..]);
			packed[^1] ^= 0x01;
			Assert.ThrowsAny<CryptographicException>(() => Encryption.Decrypt("v2:" + Convert.ToBase64String(packed)));
		}

		[Fact]
		public void A_value_in_the_old_format_is_refused()
		{
			Assert.True(Encryption.IsLegacy("bm90LXYyLWNpcGhlcg=="));
			Assert.ThrowsAny<CryptographicException>(() => Encryption.Decrypt("bm90LXYyLWNpcGhlcg=="));
		}

		[Fact]
		public void A_password_verifies_against_its_hash_and_nothing_else()
		{
			string hash = PasswordHasher.Hash("CorrectHorse9!");
			Assert.True(PasswordHasher.Verify("CorrectHorse9!", hash));
			Assert.False(PasswordHasher.Verify("correcthorse9!", hash));
			Assert.False(PasswordHasher.Verify("anything", ""));
			Assert.False(PasswordHasher.Verify("anything", "not.a.hash"));
		}
	}
}
