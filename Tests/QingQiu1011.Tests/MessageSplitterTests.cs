using QingQiu1011.Services.Discord;

namespace QingQiu1011.Tests;

public sealed class MessageSplitterTests {
	[Fact]
	public void Split_NullText_ReturnsEmpty() {
		var chunks = MessageSplitter.Split(null);

		Assert.Empty(chunks);
	}

	[Fact]
	public void Split_EmptyText_ReturnsEmpty() {
		var chunks = MessageSplitter.Split("");

		Assert.Empty(chunks);
	}

	[Fact]
	public void Split_ShortText_ReturnsSingleChunk() {
		var chunks = MessageSplitter.Split("hello");

		var chunk = Assert.Single(chunks);
		Assert.Equal("hello", chunk);
	}

	[Fact]
	public void Split_ExactLimit_ReturnsSingleChunk() {
		var text = new string('a', 1900);

		var chunks = MessageSplitter.Split(text);

		var chunk = Assert.Single(chunks);
		Assert.Equal(1900, chunk.Length);
	}

	[Fact]
	public void Split_NewlineSeparator_CutsAtNewline() {
		var chunks = MessageSplitter.Split("aaa\nbbbb", 5);

		Assert.Equal<string>(["aaa", "bbbb"], chunks);
	}

	[Fact]
	public void Split_SpaceSeparator_CutsAtSpace() {
		var chunks = MessageSplitter.Split("aaa bbbb ccc", 7);

		Assert.Equal<string>(["aaa", "bbbb", "ccc"], chunks);
	}

	[Fact]
	public void Split_NoSeparator_HardCuts() {
		var chunks = MessageSplitter.Split("aaabbbccc", 3);

		Assert.Equal<string>(["aaa", "bbb", "ccc"], chunks);
	}

	[Fact]
	public void Split_HardCutKeepsSurrogatePair() {
		var chunks = MessageSplitter.Split("ab\U0001F600cd", 3);

		Assert.Equal<string>(["ab", "\U0001F600c", "d"], chunks);
	}

	[Fact]
	public void Split_NewlineChunks_PreserveContent() {
		var chunks = MessageSplitter.Split("aaaa\nbbbb\ncccc", 9);

		Assert.Equal<string>(["aaaa", "bbbb", "cccc"], chunks);
	}

	[Fact]
	public void Split_LongText_AllChunksWithinLimit() {
		var text = string.Join("\n", Enumerable.Range(0, 500).Select(static i => new string('x', 10)));

		var chunks = MessageSplitter.Split(text);

		Assert.True(chunks.Count > 1);
		Assert.All(chunks, static chunk => Assert.True(chunk.Length <= 1900, $"chunk length was {chunk.Length}"));
	}

	[Fact]
	public void Split_LongText_NoLeadingOrTrailingNewline() {
		var text = string.Join("\n", Enumerable.Range(0, 500).Select(static i => $"line-{i}"));

		var chunks = MessageSplitter.Split(text);

		Assert.All(chunks, static chunk => {
			Assert.False(chunk.StartsWith('\n'));
			Assert.False(chunk.EndsWith('\n'));
		});
	}

	[Fact]
	public void Split_WhitespaceOnlyText_ReturnsEmpty() {
		var text = new string(' ', 5000);

		var chunks = MessageSplitter.Split(text, 100);

		Assert.Empty(chunks);
	}

	[Fact]
	public void Split_ZeroLimit_FallsBackToDefault() {
		var chunks = MessageSplitter.Split("hello", 0);

		var chunk = Assert.Single(chunks);
		Assert.Equal("hello", chunk);
	}

	[Fact]
	public void Split_CjkText_SplitsByCodeUnits() {
		var text = string.Concat(Enumerable.Repeat("花火学园", 1000));

		var chunks = MessageSplitter.Split(text);

		Assert.True(chunks.Count > 1);
		Assert.All(chunks, static chunk => Assert.True(chunk.Length <= 1900));
	}
}
