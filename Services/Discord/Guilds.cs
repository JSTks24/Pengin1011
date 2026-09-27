using Discord;
using Discord.WebSocket;
using QingQiu1011.Core.Logging;

namespace QingQiu1011.Services.Discord;

public static class Guilds {
	public static SocketGuild? Get(ulong guildId) {
		return DiscordGateway.GetGuild(guildId);
	}

	public static IRole? GetRole(ulong guildId, ulong roleId) {
		return Get(guildId)?.GetRole(roleId);
	}

	public static string? GetName(ulong guildId) {
		return Get(guildId)?.Name;
	}

	public static async Task<bool> LeaveAsync(ulong guildId) {
		var guild = Get(guildId);
		if (guild == null) return false;
		try {
			await guild.LeaveAsync();
			return true;
		} catch (Exception e) {
			Logger.Error(typeof(Guilds), e, $"退出服务器失败：{guildId}");
			return false;
		}
	}
}
