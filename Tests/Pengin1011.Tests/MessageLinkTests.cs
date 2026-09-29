using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

public sealed class MessageLinkTests {
	[Fact]
	public void TryParse_StandardLink_ReturnsParts() {
		var ok = MessageLink.TryParse("https://discord.com/channels/111/222/333", out var parts);

		Assert.True(ok);
		Assert.NotNull(parts);
		Assert.Equal(111UL, parts!.GuildId);
		Assert.Equal(222UL, parts.ChannelId);
		Assert.Equal(333UL, parts.MessageId);
	}

	[Theory]
	[InlineData("https://discord.com/channels/1/2/3")]
	[InlineData("https://ptb.discord.com/channels/1/2/3")]
	[InlineData("https://canary.discord.com/channels/1/2/3")]
	[InlineData("https://discordapp.com/channels/1/2/3")]
	[InlineData("https://DISCORD.COM/channels/1/2/3")]
	[InlineData("https://discord.com/channels/1/2/3/")]
	[InlineData("https://discord.com/channels/1/2/3?x=1")]
	[InlineData("https://discord.com/channels/1/2/3#fragment")]
	[InlineData("  https://discord.com/channels/1/2/3  ")]
	public void TryParse_KnownHostsAndDecorations_Succeeds(string input) {
		var ok = MessageLink.TryParse(input, out var parts);

		Assert.True(ok, $"input was {input}");
		Assert.NotNull(parts);
	}

	[Theory]
	[InlineData("https://example.com/channels/1/2/3")]
	[InlineData("https://evil.discord.com/channels/1/2/3")]
	[InlineData("http://discord.com/channels/1/2/3")]
	[InlineData("https://discord.com/channels/1/2")]
	[InlineData("https://discord.com/channels/abc/2/3")]
	[InlineData("https://discord.com/channels/@me/2/3")]
	[InlineData("https://discord.com/other/1/2/3")]
	[InlineData("discord.com/channels/1/2/3")]
	[InlineData("hello world")]
	[InlineData("")]
	public void TryParse_InvalidInput_ReturnsFalse(string input) {
		var ok = MessageLink.TryParse(input, out var parts);

		Assert.False(ok, $"input was {input}");
		Assert.Null(parts);
	}

	[Fact]
	public void TryParse_Null_ReturnsFalse() {
		var ok = MessageLink.TryParse(null, out var parts);

		Assert.False(ok);
		Assert.Null(parts);
	}

	[Fact]
	public void TryParse_LargeIds_ReturnsParts() {
		var ok = MessageLink.TryParse("https://discord.com/channels/999999999999999999/2/3", out var parts);

		Assert.True(ok);
		Assert.NotNull(parts);
		Assert.Equal(999999999999999999UL, parts!.GuildId);
	}
}
