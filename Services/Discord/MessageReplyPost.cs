using Discord;

namespace Pengin1011.Services.Discord;

internal sealed class MessageReplyPost : IReplyPost {
	private readonly IUserMessage _message;

	public MessageReplyPost(IUserMessage message) {
		_message = message;
	}

	public ulong Id => _message.Id;

	public Task<bool> EditAsync(string content, CancellationToken ct) {
		return Messages.EditAsync(_message, content, ct: ct);
	}

	public Task<bool> DeleteAsync(CancellationToken ct) {
		return Messages.DeleteAsync(_message, ct);
	}
}
