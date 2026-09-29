using Discord;

namespace Pengin1011.Tests;

public sealed record FakeSent(ulong Id, string? Text, AllowedMentions? AllowedMentions, MessageReference? Reference, string? Filename = null);

public class FakeMessageChannel : IMessageChannel {
	public ulong Id { get; set; }
	public Exception? SendException { get; set; }
	public List<int> FailOnCall { get; } = [];
	public List<FakeSent> Sent { get; } = [];

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
		return message;
	}

	public Task<IMessage> GetMessageAsync(ulong id, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public IAsyncEnumerable<IReadOnlyCollection<IMessage>> GetMessagesAsync(int limit = 100, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public IAsyncEnumerable<IReadOnlyCollection<IMessage>> GetMessagesAsync(ulong fromMessageId, Direction dir, int limit = 100, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) {
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

public sealed class FakeDMChannel : FakeMessageChannel, IDMChannel {
	public IUser? Recipient { get; set; }

	public IReadOnlyCollection<IUser> Recipients => Recipient != null ? [Recipient] : [];

	public Task CloseAsync(RequestOptions? options = null) {
		return Task.CompletedTask;
	}
}

public sealed class FakeUserMessage : IUserMessage {
	public ulong Id { get; set; }
	public string Content { get; set; } = "";
	public IMessageChannel? Channel { get; set; }
	public IUserMessage? ReferencedMessage { get; set; }
	public int Edits { get; private set; }
	public bool Deleted { get; private set; }
	public DateTimeOffset CreatedAt => DateTimeOffset.UnixEpoch;

	public Task ModifyAsync(Action<MessageProperties> func, RequestOptions? options = null) {
		if (options?.CancelToken.IsCancellationRequested == true) throw new OperationCanceledException(options.CancelToken);
		Edits++;
		var properties = new MessageProperties();
		func(properties);
		if (properties.Content.IsSpecified) Content = properties.Content.Value ?? "";
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
	public IReadOnlyCollection<IMessageComponent> Components => throw new NotSupportedException();
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
		Deleted = true;
		return Task.CompletedTask;
	}
}

public sealed class FakeThreadChannel : IThreadChannel {
	public ulong Id { get; set; } = 1;
	public bool IsArchived { get; set; }
	public List<FakeThreadModification> Modifications { get; } = [];
	public Func<ThreadChannelProperties, Exception?>? OnModify { get; set; }

	public sealed record FakeThreadModification(bool? Archived, IReadOnlyCollection<ulong> AppliedTags);

	public Task ModifyAsync(Action<ThreadChannelProperties> func, RequestOptions? options = null) {
		if (options?.CancelToken.IsCancellationRequested == true) throw new OperationCanceledException(options.CancelToken);
		var properties = new ThreadChannelProperties();
		func(properties);
		var failure = OnModify?.Invoke(properties);
		if (failure != null) throw failure;
		Modifications.Add(new FakeThreadModification(properties.Archived.IsSpecified ? properties.Archived.Value : null, properties.AppliedTags.IsSpecified ? [.. properties.AppliedTags.Value] : []));
		if (properties.Archived.IsSpecified) IsArchived = properties.Archived.Value;
		return Task.CompletedTask;
	}

	public Task ModifyAsync(Action<TextChannelProperties> func, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public ulong GuildId => 0;
	public IGuild Guild => throw new NotSupportedException();
	public ulong OwnerId => 0;
	public int MemberCount => 0;
	public int MessageCount => 0;
	public bool HasJoined => false;
	public bool IsLocked => false;
	public bool? IsInvitable => false;
	public DateTimeOffset ArchiveTimestamp => DateTimeOffset.UnixEpoch;
	public ThreadArchiveDuration AutoArchiveDuration => ThreadArchiveDuration.OneDay;
	public ThreadType Type => ThreadType.PublicThread;
	public ThreadArchiveDuration DefaultArchiveDuration => ThreadArchiveDuration.OneDay;
	public string Topic => "";
	public int DefaultSlowModeInterval => 0;
	public bool IsNsfw => false;
	public ChannelType ChannelType => ChannelType.PublicThread;
	public IReadOnlyCollection<ulong> AppliedTags => [];
	public IReadOnlyCollection<Overwrite> PermissionOverwrites => [];

	public string Name => "fake-thread";
	public DateTimeOffset CreatedAt => DateTimeOffset.UnixEpoch;
	public int Position => 0;
	public ulong? CategoryId => null;
	public int SlowModeInterval => 0;
	public ulong? LastMessageId => null;
	public string Mention => $"<#{Id}>";
	public ChannelFlags Flags => ChannelFlags.None;

	public Task AddUserAsync(IGuildUser user, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task RemoveUserAsync(IGuildUser user, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task JoinAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task LeaveAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task DeleteAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public OverwritePermissions? GetPermissionOverwrite(IUser user) {
		throw new NotSupportedException();
	}

	public OverwritePermissions? GetPermissionOverwrite(IRole role) {
		throw new NotSupportedException();
	}

	public Task AddPermissionOverwriteAsync(IUser user, OverwritePermissions permissions, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task AddPermissionOverwriteAsync(IRole role, OverwritePermissions permissions, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task RemovePermissionOverwriteAsync(IUser user, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task RemovePermissionOverwriteAsync(IRole role, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task SyncPermissionsAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task<IInviteMetadata> CreateInviteAsync(int? maxAge = 86400, int? maxUses = null, bool temporary = false, bool unique = false, RequestOptions? options = null, IEnumerable<ulong>? targetApplicationUserIds = null, IEnumerable<ulong>? targetRoleIds = null) {
		throw new NotSupportedException();
	}

	public Task<IInviteMetadata> CreateInviteToApplicationAsync(ulong applicationId, int? maxAge = 86400, int? maxUses = null, bool temporary = false, bool unique = false, RequestOptions? options = null, IEnumerable<ulong>? targetRoleIds = null) {
		throw new NotSupportedException();
	}

	public Task<IInviteMetadata> CreateInviteToApplicationAsync(DefaultApplications application, int? maxAge = 86400, int? maxUses = null, bool temporary = false, bool unique = false, RequestOptions? options = null, IEnumerable<ulong>? targetRoleIds = null) {
		throw new NotSupportedException();
	}

	public Task<IInviteMetadata> CreateInviteToStreamAsync(IUser user, int? maxAge = 86400, int? maxUses = null, bool temporary = false, bool unique = false, RequestOptions? options = null, IEnumerable<ulong>? targetRoleIds = null) {
		throw new NotSupportedException();
	}

	public Task<IReadOnlyCollection<IInviteMetadata>> GetInvitesAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task<ICategoryChannel> GetCategoryAsync(CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task<IWebhook> CreateWebhookAsync(string name, Stream? avatar = null, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task<IWebhook> GetWebhookAsync(ulong id, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task<IReadOnlyCollection<IWebhook>> GetWebhooksAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task<IThreadChannel> CreateThreadAsync(string name, ThreadType type = ThreadType.PublicThread, ThreadArchiveDuration autoArchiveDuration = ThreadArchiveDuration.OneDay, IMessage? message = null, bool? invitable = null, int? slowMode = null, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task<IReadOnlyCollection<IThreadChannel>> GetActiveThreadsAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task<IReadOnlyCollection<IThreadChannel>> GetPublicArchivedThreadsAsync(int? limit = null, DateTimeOffset? before = null, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task<IReadOnlyCollection<IThreadChannel>> GetPrivateArchivedThreadsAsync(int? limit = null, DateTimeOffset? before = null, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task<IReadOnlyCollection<IThreadChannel>> GetJoinedPrivateArchivedThreadsAsync(int? limit = null, DateTimeOffset? before = null, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task<IMessage> GetMessageAsync(ulong id, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public IAsyncEnumerable<IReadOnlyCollection<IMessage>> GetMessagesAsync(int limit = DiscordConfig.MaxMessagesPerBatch, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public IAsyncEnumerable<IReadOnlyCollection<IMessage>> GetMessagesAsync(ulong fromMessageId, Direction dir, int limit = DiscordConfig.MaxMessagesPerBatch, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public IAsyncEnumerable<IReadOnlyCollection<IMessage>> GetMessagesAsync(IMessage fromMessage, Direction dir, int limit = DiscordConfig.MaxMessagesPerBatch, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) {
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

	public Task DeleteMessagesAsync(IEnumerable<ulong> messageIds, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task DeleteMessagesAsync(IEnumerable<IMessage> messages, RequestOptions? options = null) {
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

	IAsyncEnumerable<IReadOnlyCollection<IUser>> IChannel.GetUsersAsync(CacheMode mode, RequestOptions? options) {
		throw new NotSupportedException();
	}

	public IAsyncEnumerable<IReadOnlyCollection<IGuildUser>> GetUsersAsync(CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	Task<IUser> IChannel.GetUserAsync(ulong id, CacheMode mode, RequestOptions? options) {
		throw new NotSupportedException();
	}

	public Task<IGuildUser> GetUserAsync(ulong id, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task ModifyAsync(Action<GuildChannelProperties> func, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task<IUserMessage> SendMessageAsync(string text, bool isTTS = false, Embed? embed = null, RequestOptions? options = null, AllowedMentions? allowedMentions = null, MessageReference? messageReference = null, MessageComponent? components = null, ISticker[]? stickers = null, Embed[]? embeds = null, MessageFlags flags = MessageFlags.None, PollProperties? poll = null) {
		throw new NotSupportedException();
	}

	public Task<IUserMessage> SendFileAsync(Stream stream, string filename, string? text = null, bool isTTS = false, Embed? embed = null, RequestOptions? options = null, bool isSpoiler = false, AllowedMentions? allowedMentions = null, MessageReference? messageReference = null, MessageComponent? components = null, ISticker[]? stickers = null, Embed[]? embeds = null, MessageFlags flags = MessageFlags.None, PollProperties? poll = null) {
		throw new NotSupportedException();
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
}

public sealed class FakeUser : IUser {
	public ulong Id { get; set; }
	public Exception? DmException { get; set; }
	public IDMChannel? DmChannel { get; set; }

	public string Username => "fake-user";
	public bool IsBot => false;
	public bool IsWebhook => false;
	public string AvatarId => throw new NotSupportedException();
	public string Discriminator => "0";
	public ushort DiscriminatorValue => 0;
	public string GlobalName => "Fake User";
	public string Mention => $"<@{Id}>";
	public UserStatus Status => UserStatus.Offline;
	public IReadOnlyCollection<ClientType> ActiveClients => [];
	public IReadOnlyCollection<IActivity> Activities => [];

	public Task<IDMChannel> CreateDMChannelAsync(RequestOptions? options = null) {
		if (DmException != null) throw DmException;
		return Task.FromResult(DmChannel ?? throw new NotSupportedException());
	}

	public string GetAvatarUrl(ImageFormat format = ImageFormat.Auto, ushort size = 128) {
		throw new NotSupportedException();
	}

	public string GetDefaultAvatarUrl() {
		throw new NotSupportedException();
	}

	public string GetDisplayAvatarUrl(ImageFormat format = ImageFormat.Auto, ushort size = 128) {
		throw new NotSupportedException();
	}

	public string GetAvatarDecorationUrl() {
		throw new NotSupportedException();
	}

	public UserProperties? PublicFlags => null;
	public string AvatarDecorationHash => throw new NotSupportedException();
	public ulong? AvatarDecorationSkuId => null;
	public PrimaryGuild? PrimaryGuild => null;
	public DateTimeOffset CreatedAt => DateTimeOffset.UnixEpoch;

	public Task UpdateAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}
}
