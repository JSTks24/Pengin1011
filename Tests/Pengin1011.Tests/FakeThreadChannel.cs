using Discord;

namespace Pengin1011.Tests;

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
