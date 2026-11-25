using Discord;

namespace Pengin1011.Tests;

public sealed class FakeDMChannel : FakeMessageChannel, IDMChannel {
	public IUser? Recipient { get; set; }

	public IReadOnlyCollection<IUser> Recipients => Recipient != null ? [Recipient] : [];

	public Task CloseAsync(RequestOptions? options = null) {
		return Task.CompletedTask;
	}
}
