using Discord;
using QingQiu1011.Core.Logging;

namespace QingQiu1011.Services.Discord;

public static class Channels {
	public static IChannel? Resolve(ulong channelId) {
		var client = DiscordGateway.Client;
		if (client == null || channelId == 0) return null;
		return client.GetChannel(channelId);
	}

	public static async Task<IChannel?> ResolveAsync(ulong channelId) {
		var client = DiscordGateway.Client;
		if (client == null || channelId == 0) return null;
		var cached = client.GetChannel(channelId);
		if (cached != null) return cached;
		try {
			return await client.GetChannelAsync(channelId);
		} catch (Exception e) {
			Logger.Error(typeof(Channels), e, $"REST 解析频道失败：{channelId}");
			return null;
		}
	}

	public static bool CanView(IGuildChannel? channel, IGuildUser? user) {
		return user != null && channel != null && user.GetPermissions(channel).ViewChannel;
	}

	public static bool CanSend(IGuildChannel? channel, IGuildUser? user) {
		return user != null && channel != null && user.GetPermissions(channel).SendMessages;
	}

	public static bool CanReadHistory(IGuildChannel? channel, IGuildUser? user) {
		return user != null && channel != null && user.GetPermissions(channel).ReadMessageHistory;
	}

	public static bool IsAdministrator(IGuildUser? user) {
		return user?.GuildPermissions.Administrator ?? false;
	}

	public static bool CanBotView(IGuildChannel? channel) {
		return HasBotPermission(channel, static permissions => permissions.ViewChannel);
	}

	public static bool CanBotSend(IGuildChannel? channel) {
		return HasBotPermission(channel, static permissions => permissions.SendMessages);
	}

	public static bool CanBotReadHistory(IGuildChannel? channel) {
		return HasBotPermission(channel, static permissions => permissions.ReadMessageHistory);
	}

	private static bool HasBotPermission(IGuildChannel? channel, Func<ChannelPermissions, bool> check) {
		if (channel == null) return false;
		var self = DiscordGateway.GetGuild(channel.GuildId)?.CurrentUser;
		return self != null && check(self.GetPermissions(channel));
	}
}
