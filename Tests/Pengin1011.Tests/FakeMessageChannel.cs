using Discord;

namespace Pengin1011.Tests;

public class FakeMessageChannel : IMessageChannel {
	public ulong Id { get; set; }
	public Exception? SendException { get; set; }
	public List<int> FailOnCall { get; } = [];
	public List<FakeSent> Sent { get; } = [];
	public List<FakeUserMessage> Messages { get; } = [];
	public List<RequestOptions?> HistoryRequests { get; } = [];
	public Func<RequestOptions?, IAsyncEnumerable<IReadOnlyCollection<IMessage>>>? ReadHistory { get; set; }

	public string Name => "fake-channel";
	public ChannelType ChannelType => ChannelType.Text;
	public DateTimeOffset CreatedAt => DateTimeOffset.UnixEpoch;

	public Task<IUserMessage> SendMessageAsync(string text, bool isTTS = false, Embed? embed = null, RequestOptions? options = null, AllowedMentions? allowedMentions = null, MessageReference? messageReference = null, MessageComponent? components = null, ISticker[]? stickers = null, Embed[]? embeds = null, MessageFlags flags = MessageFlags.None, PollProperties? poll = null) {
		if (SendException != null) throw SendException;
		var index = Sent.Count + 1;
		if (FailOnCall.Contains(index)) throw new InvalidOperationException($"第 {index} 次发送失败");
		return Task.FromResult(Send(text, null, allowedMentions, messageReference));
	}

	public Task<IUserMessage> SendFileAsync(Stream stream, string filename, string? text = null, bool isTTS = false, Embed? embed = null, RequestOptions? options = null, bool isSpoiler = false, AllowedMentions? allowedMentions = null, MessageReference? messageReference = null, MessageComponent? components = null, ISticker[]? stickers = null, Embed[]? embeds = null, MessageFlags flags = MessageFlags.None, PollProperties? poll = null) {
		if (SendException != null) throw SendException;
		return Task.FromResult(Send(text, filename, allowedMentions, messageReference));
	}

	public Task<IUserMessage> SendFileAsync(string filePath, string? text = null, bool isTTS = false, Embed? embed = null, RequestOptions? options = null, bool isSpoiler = false, AllowedMentions? allowedMentions = null, MessageReference? messageReference = null, MessageComponent? components = null, ISticker[]? stickers = null, Embed[]? embeds = null, MessageFlags flags = MessageFlags.None, PollProperties? poll = null) {
		throw new NotSupportedException();
	}

	public Task<IUserMessage> SendFileAsync(FileAttachment attachment, string? text = null, bool isTTS = false, Embed? embed = null, RequestOptions? options = null, AllowedMentions? allowedMentions = null, MessageReference? messageReference = null, MessageComponent? components = null, ISticker[]? stickers = null, Embed[]? embeds = null, MessageFlags flags = MessageFlags.None, PollProperties? poll = null) {
		throw new NotSupportedException();
	}

	public Task<IUserMessage> SendFilesAsync(IEnumerable<FileAttachment> attachments, string? text = null, bool isTTS = false, Embed? embed = null, RequestOptions? options = null, AllowedMentions? allowedMentions = null, MessageReference? messageReference = null, MessageComponent? components = null, ISticker[]? stickers = null, Embed[]? embeds = null, MessageFlags flags = MessageFlags.None, PollProperties? poll = null) {
		throw new NotSupportedException();
	}

	private IUserMessage Send(string? text, string? filename, AllowedMentions? allowedMentions, MessageReference? messageReference) {
		var message = new FakeUserMessage {
			Id = (ulong)(Sent.Count + 1),
			Content = text ?? "",
			Channel = this,
		};
		Sent.Add(new FakeSent(message.Id, text, allowedMentions, messageReference, filename));
		Messages.Add(message);
		return message;
	}

	public Task<IMessage> GetMessageAsync(ulong id, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public IAsyncEnumerable<IReadOnlyCollection<IMessage>> GetMessagesAsync(int limit = 100, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) {
		HistoryRequests.Add(options);
		if (ReadHistory != null) return ReadHistory(options);
		throw new NotSupportedException();
	}

	public IAsyncEnumerable<IReadOnlyCollection<IMessage>> GetMessagesAsync(ulong fromMessageId, Direction dir, int limit = 100, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) {
		HistoryRequests.Add(options);
		if (ReadHistory != null) return ReadHistory(options);
		throw new NotSupportedException();
	}

	public IAsyncEnumerable<IReadOnlyCollection<IMessage>> GetMessagesAsync(IMessage fromMessage, Direction dir, int limit = 100, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task<IReadOnlyCollection<IMessage>> GetPinnedMessagesAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task DeleteMessageAsync(ulong messageId, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task DeleteMessageAsync(IMessage message, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task<IUserMessage> ModifyMessageAsync(ulong messageId, Action<MessageProperties> func, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task TriggerTypingAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public IDisposable EnterTypingState(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public IAsyncEnumerable<IReadOnlyCollection<IUser>> GetUsersAsync(CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task<IUser> GetUserAsync(ulong id, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) {
		throw new NotSupportedException();
	}
}
