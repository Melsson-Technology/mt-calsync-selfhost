using System.Text;

namespace Core.MTCalSync
{
	// Graph exposes a meeting's UID as a hex GlobalObjectId (MS-OXOCAL). For an
	// iCalendar invite the blob embeds the original RFC 5545 UID in a "vCal-Uid"
	// structure, which Google exposes in clean form. This converts between the two.
	//
	// Layout:  [16-byte header][4 exception-date][8 creation-time][8 reserved]
	//          [4 size LE][ "vCal-Uid" (8) | 0x01000000 | <UID ascii> | 0x00 ]
	public static class GlobalObjectId
	{
		private static readonly byte[] Header =
			{ 0x04,0x00,0x00,0x00,0x82,0x00,0xE0,0x00,0x74,0xC5,0xB7,0x10,0x1A,0x82,0xE0,0x08 };
		private static readonly byte[] VCalUid = Encoding.ASCII.GetBytes("vCal-Uid");
		private static readonly byte[] VCalVer = { 0x01,0x00,0x00,0x00 };

		// The clean RFC UID if `value` is a hex GlobalObjectId embedding a vCal-Uid, else null.
		public static string? TryExtractUid(string? value)
		{
			byte[]? bytes = FromHex(value);
			if (bytes == null) return null;
			int marker = IndexOf(bytes, VCalUid);
			if (marker < 0) return null;
			int start = marker + VCalUid.Length + VCalVer.Length;   // skip "vCal-Uid" + version
			if (start >= bytes.Length) return null;
			int end = start;
			while (end < bytes.Length && bytes[end] != 0x00) end++;
			return end > start ? Encoding.ASCII.GetString(bytes, start, end - start) : null;
		}

		// The uppercase hex GlobalObjectId Graph stores for an external invite with this
		// UID, with zeroed timestamps as in the canonical vCal-Uid form.
		public static string EncodeUid(string cleanUid)
		{
			if (string.IsNullOrEmpty(cleanUid)) return string.Empty;
			byte[] uid = Encoding.ASCII.GetBytes(cleanUid);
			var data = new List<byte>();
			data.AddRange(VCalUid);
			data.AddRange(VCalVer);
			data.AddRange(uid);
			data.Add(0x00);

			var blob = new List<byte>();
			blob.AddRange(Header);
			blob.AddRange(new byte[4 + 8 + 8]);   // exception-date + creation-time + reserved
			uint size = (uint)data.Count;          // 4-byte size, little-endian
			blob.Add((byte)(size & 0xFF));
			blob.Add((byte)((size >> 8) & 0xFF));
			blob.Add((byte)((size >> 16) & 0xFF));
			blob.Add((byte)((size >> 24) & 0xFF));
			blob.AddRange(data);
			return ToHex(blob.ToArray());
		}

		// The clean RFC UID when `value` is a wrapped GOID, else `value` unchanged.
		public static string Normalize(string? value) => TryExtractUid(value) ?? (value ?? string.Empty);

		private static byte[]? FromHex(string? s)
		{
			if (string.IsNullOrEmpty(s)) return null;
			s = s.Trim();
			if (s.Length < 2 || (s.Length % 2) != 0) return null;
			foreach (char c in s) if (!Uri.IsHexDigit(c)) return null;
			var b = new byte[s.Length / 2];
			for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
			return b;
		}

		private static string ToHex(byte[] b)
		{
			var sb = new StringBuilder(b.Length * 2);
			foreach (var x in b) sb.Append(x.ToString("X2"));
			return sb.ToString();
		}

		private static int IndexOf(byte[] hay, byte[] needle)
		{
			for (int i = 0; i <= hay.Length - needle.Length; i++)
			{
				bool ok = true;
				for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
				if (ok) return i;
			}
			return -1;
		}
	}
}
