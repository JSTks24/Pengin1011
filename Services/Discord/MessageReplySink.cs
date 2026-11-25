using Pengin1011.Core;
using Discord;
using Pengin1011.Core.Logging;

namespace Pengin1011.Services.Discord;

internal sealed class MessageReplySink : IReplySink {
	private readonly IMessageChannel? _channel;

	public MessageReplySink(IMessage source) {
		_channel = source.Channel as IMessageChannel;
	}

	public async Task<IReplyPost?> ReplyAsync(string content, CancellationToken ct) {
		var result = await Messages.SendAsync(_channel, content, ct: ct);
		if (!result.Success) {
			if (result.Status == MessageSendStatus.Partial) {
				Logger.Error(typeof(StreamingReply), Loc.Format("StreamPartialDelivery", result.Sent.Count, result.PlannedChunks));
			}
			return null;
		}
		return new MessageReplyPost(result.First!);
	}
}
