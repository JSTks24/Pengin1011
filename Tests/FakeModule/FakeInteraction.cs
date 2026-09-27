using Discord;

namespace QingQiu1011.Modules.FakeModule;

public sealed class FakeInteractionContext : IInteractionContext {
	public FakeInteractionContext(FakeInteraction interaction) {
		Interaction = interaction;
	}

	public IDiscordClient Client => null!;
	public IGuild Guild => null!;
	public IMessageChannel Channel => null!;
	public IUser User => null!;
	public IDiscordInteraction Interaction { get; }
}

public sealed class FakeInteraction : ISlashCommandInteraction {
	public FakeInteraction(string commandName) {
		Data = new FakeCommandData(commandName);
	}

	public List<string> Responses { get; } = [];
	public List<string> Followups { get; } = [];
	public bool Deferred { get; private set; }
	public bool ThrowOnRespond { get; set; }

	public IApplicationCommandInteractionData Data { get; }
	IDiscordInteractionData IDiscordInteraction.Data => Data;
	public ulong Id => 1;
	public InteractionType Type => InteractionType.ApplicationCommand;
	public string Token => "";
	public int Version => 1;
	public bool HasResponded { get; set; }
	public IUser User => null!;
	public string UserLocale => "";
	public string GuildLocale => "";
	public bool IsDMInteraction => true;
	public ulong? ChannelId => null;
	public ulong? GuildId => null;
	public ulong ApplicationId => 0;
	public IReadOnlyCollection<IEntitlement> Entitlements => [];
	public IReadOnlyDictionary<ApplicationIntegrationType, ulong> IntegrationOwners => new Dictionary<ApplicationIntegrationType, ulong>();
	public InteractionContextType? ContextType => null;
	public GuildPermissions Permissions => new(0);
	public ulong AttachmentSizeLimit => 0;
	public DateTimeOffset CreatedAt => DateTimeOffset.UnixEpoch;

	public Task RespondAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false, AllowedMentions? allowedMentions = null, MessageComponent? components = null, Embed? embed = null, RequestOptions? options = null, PollProperties? poll = null, MessageFlags flags = MessageFlags.None) {
		if (ThrowOnRespond) throw new InvalidOperationException("伪造响应失败");
		HasResponded = true;
		Responses.Add(text ?? "");
		return Task.CompletedTask;
	}

	public Task RespondWithFileAsync(Stream fileStream, string filename, string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false, AllowedMentions? allowedMentions = null, MessageComponent? components = null, Embed? embed = null, RequestOptions? options = null, PollProperties? poll = null, MessageFlags flags = MessageFlags.None) {
		throw new NotSupportedException();
	}

	public Task RespondWithFileAsync(string filePath, string filename, string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false, AllowedMentions? allowedMentions = null, MessageComponent? components = null, Embed? embed = null, RequestOptions? options = null, PollProperties? poll = null, MessageFlags flags = MessageFlags.None) {
		throw new NotSupportedException();
	}

	public Task RespondWithFileAsync(FileAttachment attachment, string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false, AllowedMentions? allowedMentions = null, MessageComponent? components = null, Embed? embed = null, RequestOptions? options = null, PollProperties? poll = null, MessageFlags flags = MessageFlags.None) {
		throw new NotSupportedException();
	}

	public Task RespondWithFilesAsync(IEnumerable<FileAttachment> attachments, string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false, AllowedMentions? allowedMentions = null, MessageComponent? components = null, Embed? embed = null, RequestOptions? options = null, PollProperties? poll = null, MessageFlags flags = MessageFlags.None) {
		throw new NotSupportedException();
	}

	public Task<IUserMessage> FollowupAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false, AllowedMentions? allowedMentions = null, MessageComponent? components = null, Embed? embed = null, RequestOptions? options = null, PollProperties? poll = null, MessageFlags flags = MessageFlags.None) {
		Followups.Add(text ?? "");
		return Task.FromResult<IUserMessage>(null!);
	}

	public Task<IUserMessage> FollowupWithFileAsync(Stream fileStream, string filename, string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false, AllowedMentions? allowedMentions = null, MessageComponent? components = null, Embed? embed = null, RequestOptions? options = null, PollProperties? poll = null, MessageFlags flags = MessageFlags.None) {
		throw new NotSupportedException();
	}

	public Task<IUserMessage> FollowupWithFileAsync(string filePath, string filename, string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false, AllowedMentions? allowedMentions = null, MessageComponent? components = null, Embed? embed = null, RequestOptions? options = null, PollProperties? poll = null, MessageFlags flags = MessageFlags.None) {
		throw new NotSupportedException();
	}

	public Task<IUserMessage> FollowupWithFileAsync(FileAttachment attachment, string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false, AllowedMentions? allowedMentions = null, MessageComponent? components = null, Embed? embed = null, RequestOptions? options = null, PollProperties? poll = null, MessageFlags flags = MessageFlags.None) {
		throw new NotSupportedException();
	}

	public Task<IUserMessage> FollowupWithFilesAsync(IEnumerable<FileAttachment> attachments, string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false, AllowedMentions? allowedMentions = null, MessageComponent? components = null, Embed? embed = null, RequestOptions? options = null, PollProperties? poll = null, MessageFlags flags = MessageFlags.None) {
		throw new NotSupportedException();
	}

	public Task<IUserMessage> GetOriginalResponseAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task<IUserMessage> ModifyOriginalResponseAsync(Action<MessageProperties> func, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task DeleteOriginalResponseAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task DeferAsync(bool ephemeral = true, RequestOptions? options = null) {
		Deferred = true;
		HasResponded = true;
		return Task.CompletedTask;
	}

	public Task RespondWithModalAsync(Modal modal, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task RespondWithPremiumRequiredAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}
}

public sealed class FakeCommandData : IApplicationCommandInteractionData {
	public FakeCommandData(string name) {
		Name = name;
	}

	public string Name { get; }
	public ulong Id => 1;
	public ApplicationCommandType Type => ApplicationCommandType.Slash;
	public IReadOnlyCollection<IApplicationCommandInteractionDataOption> Options => [];
}
