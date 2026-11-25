namespace Pengin1011.Services.Discord;

public sealed class StreamingReplyOptions {
	public int Limit { get; init; } = MessageSplitter.DefaultLimit;
	public int EditIntervalMs { get; init; } = 3000;
}
