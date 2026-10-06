using Discord;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

public sealed class ChannelsTests {
	[Fact]
	public void CanSendIn_ThreadChannel_UsesSendMessagesInThreads() {
		var permissions = new ChannelPermissions((ulong)ChannelPermission.SendMessagesInThreads);

		Assert.True(Channels.CanSendIn(permissions, new FakeThreadChannel()));
	}

	[Fact]
	public void CanSendIn_ThreadChannel_IgnoresSendMessages() {
		var permissions = new ChannelPermissions((ulong)ChannelPermission.SendMessages);

		Assert.False(Channels.CanSendIn(permissions, new FakeThreadChannel()));
	}

	[Fact]
	public void CanSendIn_GuildChannel_UsesSendMessages() {
		var sendOnly = new ChannelPermissions((ulong)ChannelPermission.SendMessages);
		var threadOnly = new ChannelPermissions((ulong)ChannelPermission.SendMessagesInThreads);

		Assert.True(Channels.CanSendIn(sendOnly, new FakeGuildChannel()));
		Assert.False(Channels.CanSendIn(threadOnly, new FakeGuildChannel()));
	}

	[Fact]
	public void CanSend_NullInputs_ReturnsFalse() {
		Assert.False(Channels.CanSend(null, null));
	}
}
