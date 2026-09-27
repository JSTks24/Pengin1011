using QingQiu1011.Services.Discord;

namespace QingQiu1011.Tests;

public sealed class MentionsTests {
	[Fact]
	public void User_ReturnsUserMentionFormat() {
		var result = Mentions.User(123UL);

		Assert.Equal("<@123>", result);
	}

	[Fact]
	public void Role_ReturnsRoleMentionFormat() {
		var result = Mentions.Role(456UL);

		Assert.Equal("<@&456>", result);
	}

	[Fact]
	public void Channel_ReturnsChannelMentionFormat() {
		var result = Mentions.Channel(789UL);

		Assert.Equal("<#789>", result);
	}

	[Fact]
	public void Timestamp_ReturnsFormattedTimestamp() {
		var time = DateTimeOffset.FromUnixTimeSeconds(1700000000);

		var result = Mentions.Timestamp(time, 'R');

		Assert.Equal("<t:1700000000:R>", result);
	}

	[Fact]
	public void Timestamp_DefaultStyleIsFull() {
		var time = DateTimeOffset.FromUnixTimeSeconds(1700000000);

		var result = Mentions.Timestamp(time);

		Assert.Equal("<t:1700000000:F>", result);
	}

	[Fact]
	public void StripUserMentions_RemovesPlainAndDecoratedMentions() {
		var result = Mentions.StripUserMentions("<@111>hello<@!222>world");

		Assert.Equal("helloworld", result);
	}

	[Fact]
	public void StripUserMentions_DoesNotCollapseSurroundingSpaces() {
		var result = Mentions.StripUserMentions("hi <@1> bye");

		Assert.Equal("hi  bye", result);
	}

	[Fact]
	public void StripUserMentions_KeepsRoleAndChannelMentions() {
		var result = Mentions.StripUserMentions("<@&1> <#2> <@3>");

		Assert.Equal("<@&1> <#2>", result);
	}

	[Fact]
	public void StripUserMentions_TrimsResult() {
		var result = Mentions.StripUserMentions("  <@5>  ");

		Assert.Equal("", result);
	}

	[Fact]
	public void StripUserMentions_NoMentions_ReturnsTrimmedOriginal() {
		var result = Mentions.StripUserMentions(" hello world  ");

		Assert.Equal("hello world", result);
	}

	[Fact]
	public void StripUserMentions_IncompleteMention_KeptAsIs() {
		var result = Mentions.StripUserMentions("a <@12 b <@ c>");

		Assert.Equal("a <@12 b <@ c>", result);
	}

	[Fact]
	public void StripUserMentions_Null_ReturnsEmpty() {
		var result = Mentions.StripUserMentions(null);

		Assert.Equal("", result);
	}

	[Fact]
	public void StripUserMentions_Empty_ReturnsEmpty() {
		var result = Mentions.StripUserMentions("");

		Assert.Equal("", result);
	}
}
