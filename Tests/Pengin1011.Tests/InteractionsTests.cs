using Discord;
using Pengin1011.Modules.FakeModule;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

public sealed class InteractionsTests {
	[Fact]
	public async Task EditOriginalAsync_SetsAllowedMentionsNone() {
		var interaction = new FakeInteraction("fakeping");

		Assert.True(await Interactions.EditOriginalAsync(interaction, "hello <@1>"));

		Assert.Equal("hello <@1>", interaction.LastOriginalContent);
		Assert.Same(AllowedMentions.None, interaction.LastOriginalAllowedMentions);
	}

	[Fact]
	public async Task EditOriginalAsync_NullInteraction_ReturnsFalse() {
		Assert.False(await Interactions.EditOriginalAsync(null, "text"));
	}
}
