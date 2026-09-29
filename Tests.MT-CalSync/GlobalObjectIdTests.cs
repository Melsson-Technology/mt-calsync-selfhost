namespace Tests.MTCalSync
{
	// Microsoft Graph reports an external invite's iCalUID wrapped in an Outlook GlobalObjectId;
	// Google reports it clean. The two copies of one meeting only match once both are reduced to
	// the clean form.
	public class GlobalObjectIdTests
	{
		private const string CleanUid = "79WaQdeWeV2iqQpDRuc1WD@Cal.com";

		[Fact]
		public void An_encoded_uid_decodes_to_itself()
		{
			string goid = GlobalObjectId.EncodeUid(CleanUid);
			Assert.StartsWith("040000008200E00074C5B7101A82E008", goid, StringComparison.OrdinalIgnoreCase);
			Assert.Equal(CleanUid, GlobalObjectId.TryExtractUid(goid));
		}

		[Fact]
		public void Normalize_unwraps_a_GlobalObjectId_and_leaves_a_clean_uid_alone()
		{
			Assert.Equal(CleanUid, GlobalObjectId.Normalize(GlobalObjectId.EncodeUid(CleanUid)));
			Assert.Equal(CleanUid, GlobalObjectId.Normalize(CleanUid));
			Assert.Equal(string.Empty, GlobalObjectId.Normalize(null));
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("not-hex-at-all")]
		[InlineData("040000008200E00074C5B7101A82E008")]   // the header with nothing after it
		public void Anything_that_is_not_a_wrapped_uid_yields_nothing(string? value) =>
			Assert.Null(GlobalObjectId.TryExtractUid(value));
	}
}
