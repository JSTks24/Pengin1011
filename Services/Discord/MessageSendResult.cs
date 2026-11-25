using Discord;

namespace Pengin1011.Services.Discord;

public sealed record MessageSendResult(MessageSendStatus Status, int PlannedChunks, IReadOnlyList<IUserMessage> Sent, IReadOnlyList<int> FailedChunkIndexes) {
	public static MessageSendResult Empty => new(MessageSendStatus.Failed, 0, [], []);

	public bool Success => Status == MessageSendStatus.Succeeded;

	public IUserMessage? First => Sent.Count > 0 ? Sent[0] : null;
}
