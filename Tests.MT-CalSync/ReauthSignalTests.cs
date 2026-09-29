namespace Tests.MTCalSync
{
	// A dead OAuth grant is raised by the token layer from inside a provider SDK call, so it
	// reaches a provider's error translation wrapped. It must come out as the reauth signal,
	// which asks the owner to reconnect, not as a retryable provider error.
	public class ReauthSignalTests
	{
		private static readonly NeedsReauthException Signal = new(42, Providers.M365, "Refresh token rejected (invalid_grant).");

		private static bool Surfaces(Exception wrapper)
		{
			try { NeedsReauthException.ThrowIfWrapped(wrapper); return false; }
			catch (NeedsReauthException e) { return ReferenceEquals(e, Signal); }
		}

		[Fact]
		public void The_signal_itself_is_rethrown() => Assert.True(Surfaces(Signal));

		[Fact]
		public void A_signal_wrapped_in_another_exception_is_rethrown() =>
			Assert.True(Surfaces(new InvalidOperationException("transport", new HttpRequestException("send", Signal))));

		[Fact]
		public void A_signal_inside_an_aggregate_is_rethrown() =>
			Assert.True(Surfaces(new AggregateException(new TimeoutException(), Signal)));

		[Fact]
		public void An_ordinary_error_passes_through() =>
			Assert.False(Surfaces(new HttpRequestException("connection reset")));
	}
}
