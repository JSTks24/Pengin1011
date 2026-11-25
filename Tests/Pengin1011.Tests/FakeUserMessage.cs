using Discord;

namespace Pengin1011.Tests;

public sealed class FakeUserMessage : IUserMessage {
	public ulong Id { get; set; }
	public string Content { get; set; } = "";
	public IMessageChannel? Channel { get; set; }
	public IUserMessage? ReferencedMessage { get; set; }
	public int Edits { get; private set; }
	public bool Deleted { get; private set; }
	public AllowedMentions? LastAllowedMentions { get; private set; }
	public Exception? DeleteException { get; set; }
	public IReadOnlyCollection<IMessageComponent> Components { get; set; } = [];
	public RequestOptions? LastModifyOptions { get; private set; }
	public DateTimeOffset CreatedAt => DateTimeOffset.UnixEpoch;

	public Task ModifyAsync(Action<MessageProperties> func, RequestOptions? options = null) {
		LastModifyOptions = options;
		if (options?.CancelToken.IsCancellationRequested == true) throw new OperationCanceledException(options.CancelToken);
		Edits++;
		var properties = new MessageProperties();
		func(properties);
		if (properties.Content.IsSpecified) Content = properties.Content.Value ?? "";
		LastAllowedMentions = properties.AllowedMentions.IsSpecified ? properties.AllowedMentions.Value : null;
		return Task.CompletedTask;
	}

	public bool IsTTS => false;
	public bool IsPinned => false;
	public bool IsSuppressed => false;
	public bool MentionedEveryone => false;
	public string CleanContent => Content;
	public DateTimeOffset Timestamp => DateTimeOffset.UnixEpoch;
	public DateTimeOffset? EditedTimestamp => null;
	public IUser Author => throw new NotSupportedException();
	public IThreadChannel Thread => throw new NotSupportedException();
	public IReadOnlyCollection<IAttachment> Attachments => throw new NotSupportedException();
	public IReadOnlyCollection<IEmbed> Embeds => throw new NotSupportedException();
	public IReadOnlyCollection<ITag> Tags => throw new NotSupportedException();
	public IReadOnlyCollection<ulong> MentionedChannelIds => throw new NotSupportedException();
	public IReadOnlyCollection<ulong> MentionedRoleIds => throw new NotSupportedException();
	public IReadOnlyCollection<ulong> MentionedUserIds => throw new NotSupportedException();
	public MessageActivity Activity => throw new NotSupportedException();
	public MessageApplication Application => throw new NotSupportedException();
	public MessageReference Reference => throw new NotSupportedException();
	public IReadOnlyDictionary<IEmote, ReactionMetadata> Reactions => throw new NotSupportedException();
	public IReadOnlyCollection<IStickerItem> Stickers => throw new NotSupportedException();
	public MessageFlags? Flags => null;
	public IMessageInteraction Interaction => throw new NotSupportedException();
	public MessageRoleSubscriptionData RoleSubscriptionData => throw new NotSupportedException();
	public PurchaseNotification PurchaseNotification => throw new NotSupportedException();
	public MessageCallData? CallData => null;

	public Task PinAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task UnpinAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task CrosspostAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public string Resolve(TagHandling userHandling = TagHandling.Name, TagHandling channelHandling = TagHandling.Name, TagHandling roleHandling = TagHandling.Name, TagHandling everyoneHandling = TagHandling.Ignore, TagHandling emojiHandling = TagHandling.Name) {
		throw new NotSupportedException();
	}

	public Task EndPollAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public IAsyncEnumerable<IReadOnlyCollection<IUser>> GetPollAnswerVotersAsync(uint answerId, int? limit = null, ulong? afterId = null, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public MessageResolvedData ResolvedData => throw new NotSupportedException();
	public IMessageInteractionMetadata InteractionMetadata => throw new NotSupportedException();
	public IReadOnlyCollection<MessageSnapshot> ForwardedMessages => throw new NotSupportedException();
	public Poll? Poll => null;

	public Task AddReactionAsync(IEmote emote, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task RemoveReactionAsync(IEmote emote, IUser user, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task RemoveReactionAsync(IEmote emote, ulong userId, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task RemoveAllReactionsAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task RemoveAllReactionsForEmoteAsync(IEmote emote, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public IAsyncEnumerable<IReadOnlyCollection<IUser>> GetReactionUsersAsync(IEmote emoji, int limit, RequestOptions? options = null, ReactionType type = ReactionType.Normal) {
		throw new NotSupportedException();
	}

	public MessageType Type => MessageType.Default;
	public MessageSource Source => MessageSource.User;

	public Task DeleteAsync(RequestOptions? options = null) {
		if (options?.CancelToken.IsCancellationRequested == true) throw new OperationCanceledException(options.CancelToken);
		if (DeleteException != null) throw DeleteException;
		Deleted = true;
		return Task.CompletedTask;
	}
}
