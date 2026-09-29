using System.Net;
using Discord;
using Discord.Net;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;

namespace Pengin1011.Services.Discord;

public enum DirectMessageResult {
	Sent,
	Partial,
	Blocked,
	Failed,
}

public enum MessageSendStatus {
	Succeeded,
	Partial,
	Failed,
}

public sealed record MessageSendResult(MessageSendStatus Status, int PlannedChunks, IReadOnlyList<IUserMessage> Sent, IReadOnlyList<int> FailedChunkIndexes) {
	public static MessageSendResult Empty => new(MessageSendStatus.Failed, 0, [], []);

	public bool Success => Status == MessageSendStatus.Succeeded;

	public IUserMessage? First => Sent.Count > 0 ? Sent[0] : null;
}

public sealed record BulkSendResult(IMessageChannel Channel, MessageSendResult Result) {
	public bool Success => Result.Status == MessageSendStatus.Succeeded;
}

public static class Messages {
	public const int MessageLimit = MessageSplitter.DefaultLimit;
	public const int EditLimit = 2000;
	public const int SendBulkDelayMs = 500;

	public static async Task<MessageSendResult> ReplyAsync(IMessage? source, string? text, AllowedMentions? allowedMentions = null, CancellationToken ct = default) {
		if (source == null || string.IsNullOrEmpty(text)) return MessageSendResult.Empty;
		if (source.Channel is not IMessageChannel channel) return MessageSendResult.Empty;
		var chunks = MessageSplitter.Split(text);
		if (chunks.Count == 0) return MessageSendResult.Empty;

		var guildId = (channel as IGuildChannel)?.GuildId;
		var reference = new MessageReference(source.Id, channel.Id, guildId, false);
		return await SendChunksAsync(channel, chunks, allowedMentions ?? AllowedMentions.None, reference, ct);
	}

	public static async Task<MessageSendResult> SendAsync(IMessageChannel? channel, string? text, AllowedMentions? allowedMentions = null, CancellationToken ct = default) {
		if (channel == null || string.IsNullOrEmpty(text)) return MessageSendResult.Empty;
		var chunks = MessageSplitter.Split(text);
		if (chunks.Count == 0) return MessageSendResult.Empty;
		return await SendChunksAsync(channel, chunks, allowedMentions ?? AllowedMentions.None, null, ct);
	}

	private static async Task<MessageSendResult> SendChunksAsync(IMessageChannel channel, IReadOnlyList<string> chunks, AllowedMentions mentions, MessageReference? reference, CancellationToken ct) {
		var sent = new List<IUserMessage>();
		var failed = new List<int>();
		try {
			var first = await channel.SendMessageAsync(chunks[0], allowedMentions: mentions, messageReference: reference, options: MakeOptions(ct));
			sent.Add(first);
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception e) {
			Logger.Error(typeof(Messages), e, Localizer.Format("MessageSendFailed", channel.Id));
			return new MessageSendResult(MessageSendStatus.Failed, chunks.Count, sent, [0]);
		}
		for (var i = 1; i < chunks.Count; i++) {
			try {
				sent.Add(await channel.SendMessageAsync(chunks[i], allowedMentions: mentions, options: MakeOptions(ct)));
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception e) {
				Logger.Error(typeof(Messages), e, Localizer.Format("MessageChunkSendFailed", channel.Id, i + 1, chunks.Count));
				failed.Add(i);
				break;
			}
		}
		var status = failed.Count == 0 ? MessageSendStatus.Succeeded : MessageSendStatus.Partial;
		return new MessageSendResult(status, chunks.Count, sent, failed);
	}

	private static RequestOptions? MakeOptions(CancellationToken ct) {
		return ct.CanBeCanceled ? new RequestOptions { CancelToken = ct } : null;
	}

	public static async Task<IUserMessage?> SendEmbedAsync(IMessageChannel? channel, Embed? embed, string? text = null, AllowedMentions? allowedMentions = null) {
		if (channel == null || embed == null) return null;
		try {
			return await channel.SendMessageAsync(text, embed: embed, allowedMentions: allowedMentions ?? AllowedMentions.None);
		} catch (Exception e) {
			Logger.Error(typeof(Messages), e, Localizer.Format("EmbedSendFailed", channel.Id));
			return null;
		}
	}

	public static async Task<IUserMessage?> SendFileAsync(IMessageChannel? channel, byte[]? data, string filename, string? text = null, AllowedMentions? allowedMentions = null) {
		if (channel == null || data == null || data.Length == 0 || string.IsNullOrEmpty(filename)) return null;
		try {
			using var stream = new MemoryStream(data);
			return await channel.SendFileAsync(stream, filename, text, allowedMentions: allowedMentions ?? AllowedMentions.None);
		} catch (Exception e) {
			Logger.Error(typeof(Messages), e, Localizer.Format("FileSendFailed", channel.Id, filename));
			return null;
		}
	}

	public static async Task<bool> EditAsync(IUserMessage? message, string? text, CancellationToken ct = default) {
		if (message == null || string.IsNullOrEmpty(text)) return false;
		try {
			var content = text.Length <= EditLimit ? text : text[..EditLimit];
			await message.ModifyAsync(properties => properties.Content = content, MakeOptions(ct));
			return true;
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception e) {
			Logger.Error(typeof(Messages), e, Localizer.Format("MessageEditFailed", message.Id));
			return false;
		}
	}

	public static async Task<bool> DeleteAsync(IMessage? message, CancellationToken ct = default) {
		if (message == null) return false;
		try {
			await message.DeleteAsync(MakeOptions(ct));
			return true;
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception e) {
			Logger.Error(typeof(Messages), e, Localizer.Format("MessageDeleteFailed", message.Id));
			return false;
		}
	}

	public static async Task<bool> ForwardAsync(IMessage? message, IMessageChannel? destination) {
		if (message == null || destination == null) return false;
		try {
			var guildId = (message.Channel as IGuildChannel)?.GuildId;
			var reference = new MessageReference(message.Id, message.Channel?.Id, guildId, null, MessageReferenceType.Forward);
			await destination.SendMessageAsync(null, messageReference: reference, flags: MessageFlags.SuppressNotification);
			return true;
		} catch (Exception e) {
			Logger.Error(typeof(Messages), e, Localizer.Format("MessageForwardFailed", message.Id, destination.Id));
			return false;
		}
	}

	public static async Task<DirectMessageResult> SendDirectAsync(ulong userId, string? text, CancellationToken ct = default) {
		if (userId == 0 || string.IsNullOrEmpty(text)) return DirectMessageResult.Failed;
		var client = DiscordGateway.Client;
		if (client == null) return DirectMessageResult.Failed;

		IUser? user = client.GetUser(userId);
		if (user == null) {
			try {
				user = await client.GetUserAsync(userId);
			} catch (Exception e) {
				Logger.Error(typeof(Messages), e, Localizer.Format("UserResolveRestFailed", userId));
				user = null;
			}
		}
		if (user == null) return DirectMessageResult.Failed;
		return await SendDirectAsync(user, text, ct);
	}

	public static async Task<DirectMessageResult> SendDirectAsync(IUser? user, string? text, CancellationToken ct = default) {
		if (user == null || string.IsNullOrEmpty(text)) return DirectMessageResult.Failed;
		var chunks = MessageSplitter.Split(text);
		if (chunks.Count == 0) return DirectMessageResult.Failed;

		try {
			await user.SendMessageAsync(chunks[0], allowedMentions: AllowedMentions.None, options: MakeOptions(ct));
		} catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.Forbidden) {
			Logger.Error(typeof(Messages), ex, Localizer.Format("DirectMessageBlocked", user.Id));
			return DirectMessageResult.Blocked;
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception e) {
			Logger.Error(typeof(Messages), e, Localizer.Format("DirectMessageSendFailed", user.Id));
			return DirectMessageResult.Failed;
		}
		for (var i = 1; i < chunks.Count; i++) {
			try {
				await user.SendMessageAsync(chunks[i], allowedMentions: AllowedMentions.None, options: MakeOptions(ct));
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception e) {
				Logger.Error(typeof(Messages), e, Localizer.Format("DirectMessageChunkSendFailed", user.Id, i + 1, chunks.Count));
				return DirectMessageResult.Partial;
			}
		}
		return DirectMessageResult.Sent;
	}

	public static async Task<IUserMessage?> SendDirectFileAsync(IUser? user, byte[]? data, string filename, string? text = null) {
		if (user == null || data == null || data.Length == 0 || string.IsNullOrEmpty(filename)) return null;
		try {
			var channel = await user.CreateDMChannelAsync();
			using var stream = new MemoryStream(data);
			return await channel.SendFileAsync(stream, filename, text);
		} catch (Exception e) {
			Logger.Error(typeof(Messages), e, Localizer.Format("DirectMessageFileSendFailed", user.Id, filename));
			return null;
		}
	}

	public static async Task<IMessage?> FetchAsync(ulong channelId, ulong messageId) {
		if (channelId == 0 || messageId == 0) return null;
		var channel = await Channels.ResolveAsync(channelId);
		if (channel is not IMessageChannel messageChannel) return null;
		try {
			return await messageChannel.GetMessageAsync(messageId);
		} catch (Exception e) {
			Logger.Error(typeof(Messages), e, Localizer.Format("MessageFetchFailed", channelId, messageId));
			return null;
		}
	}

	public static IMessage? ResolveReference(IMessage? message) {
		return message is IUserMessage userMessage ? userMessage.ReferencedMessage : null;
	}

	public static async Task<IReadOnlyList<BulkSendResult>> SendBulkAsync(IReadOnlyCollection<IMessageChannel>? channels, string? text, int delayMs = SendBulkDelayMs, CancellationToken ct = default) {
		var results = new List<BulkSendResult>();
		if (channels == null || channels.Count == 0) return results;

		var targets = channels as IReadOnlyList<IMessageChannel> ?? channels.ToList();
		for (var i = 0; i < targets.Count; i++) {
			var result = await SendAsync(targets[i], text, ct: ct);
			results.Add(new BulkSendResult(targets[i], result));
			if (delayMs > 0 && i < targets.Count - 1) {
				await Task.Delay(delayMs, ct);
			}
		}
		return results;
	}
}
