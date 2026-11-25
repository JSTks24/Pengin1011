using Discord;

namespace Pengin1011.Services.Discord;

public sealed record BulkSendResult(IMessageChannel Channel, MessageSendResult Result) {
	public bool Success => Result.Status == MessageSendStatus.Succeeded;
}
