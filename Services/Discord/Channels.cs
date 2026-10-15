using Discord;
using Pengin1011.Core;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;

namespace Pengin1011.Services.Discord;

public static class Channels {
	public static IChannel? Resolve(ulong channelId) {
		var client = DiscordGateway.Client;
		if (client == null || channelId == 0) return null;
		return client.GetChannel(channelId);
	}

	public static async Task<IChannel?> ResolveAsync(ulong channelId, CancellationToken ct = default) {
		var client = DiscordGateway.Client;
		if (client == null || channelId == 0) return null;
		var cached = client.GetChannel(channelId);
		if (cached != null) return cached;
		try {
			return await client.GetChannelAsync(channelId, options: ct.CanBeCanceled ? new RequestOptions { CancelToken = ct } : null);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Logger.Error(typeof(Channels), e, Loc.Format("ChannelResolveRestFailed", channelId));
			return null;
		}
	}

	public static bool CanView(IGuildChannel? channel, IGuildUser? user) {
		return user != null && channel != null && user.GetPermissions(channel).ViewChannel;
	}

	public static bool CanSend(IGuildChannel? channel, IGuildUser? user) {
		return user != null && channel != null && CanSendIn(user.GetPermissions(channel), channel);
	}

	public static bool CanReadHistory(IGuildChannel? channel, IGuildUser? user) {
		return user != null && channel != null && user.GetPermissions(channel).ReadMessageHistory;
	}

	public static bool IsAdministrator(IGuildUser? user) {
		return user?.GuildPermissions.Administrator ?? false;
	}

	public static bool CanBotView(IGuildChannel? channel) {
		return HasBotPermission(channel, static (permissions, _) => permissions.ViewChannel);
	}

	public static bool CanBotSend(IGuildChannel? channel) {
		return HasBotPermission(channel, CanSendIn);
	}

	public static bool CanBotReadHistory(IGuildChannel? channel) {
		return HasBotPermission(channel, static (permissions, _) => permissions.ReadMessageHistory);
	}

	internal static bool CanSendIn(ChannelPermissions permissions, IGuildChannel channel) {
		return channel is IThreadChannel ? permissions.SendMessagesInThreads : permissions.SendMessages;
	}

	private static bool HasBotPermission(IGuildChannel? channel, Func<ChannelPermissions, IGuildChannel, bool> check) {
		if (channel == null) return false;
		var self = DiscordGateway.GetGuild(channel.GuildId)?.CurrentUser;
		return self != null && check(self.GetPermissions(channel), channel);
	}
}
