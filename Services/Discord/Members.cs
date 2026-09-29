using Discord;
using Discord.WebSocket;
using Pengin1011.Core.Logging;

namespace Pengin1011.Services.Discord;

public enum RoleOpResult {
	Ok,
	MemberNotFound,
	RoleNotFound,
	Failed,
}

public static class Members {
	public static IGuildUser? Resolve(ulong guildId, ulong userId) {
		return DiscordGateway.GetGuild(guildId)?.GetUser(userId);
	}

	public static async Task<IGuildUser?> ResolveAsync(ulong guildId, ulong userId) {
		var cached = Resolve(guildId, userId);
		if (cached != null) return cached;
		var guild = DiscordGateway.GetGuild(guildId);
		if (guild == null) return null;
		try {
			return await ((IGuild)guild).GetUserAsync(userId);
		} catch (Exception e) {
			Logger.Error(typeof(Members), e, $"REST 解析成员失败：guild={guildId} user={userId}");
			return null;
		}
	}

	public static bool HasAnyRole(IGuildUser? member, IReadOnlyCollection<ulong> roleIds) {
		if (member == null || roleIds.Count == 0) return false;
		foreach (var roleId in member.RoleIds) {
			if (roleIds.Contains(roleId)) return true;
		}
		return false;
	}

	public static async Task<RoleOpResult> RemoveRolesAsync(ulong guildId, ulong userId, IReadOnlyCollection<ulong> roleIds) {
		return await ModifyRolesAsync(guildId, userId, roleIds, remove: true);
	}

	public static async Task<RoleOpResult> AddRolesAsync(ulong guildId, ulong userId, IReadOnlyCollection<ulong> roleIds) {
		return await ModifyRolesAsync(guildId, userId, roleIds, remove: false);
	}

	private static async Task<RoleOpResult> ModifyRolesAsync(ulong guildId, ulong userId, IReadOnlyCollection<ulong> roleIds, bool remove) {
		if (roleIds.Count == 0) return RoleOpResult.Ok;
		var guild = DiscordGateway.GetGuild(guildId);
		if (guild == null) return RoleOpResult.Failed;

		var member = await ResolveAsync(guildId, userId);
		if (member == null) return RoleOpResult.MemberNotFound;

		var roles = new List<IRole>();
		foreach (var roleId in roleIds) {
			var role = guild.GetRole(roleId);
			if (role == null) return RoleOpResult.RoleNotFound;
			roles.Add(role);
		}

		try {
			if (remove) await member.RemoveRolesAsync(roles); else await member.AddRolesAsync(roles);
			return RoleOpResult.Ok;
		} catch (Exception e) {
			Logger.Error(typeof(Members), e, $"{(remove ? "移除" : "添加")}身份组失败：guild={guildId} user={userId} roles=[{string.Join(",", roleIds)}]");
			return RoleOpResult.Failed;
		}
	}

	public static async Task<bool> DownloadMembersAsync(ulong guildId) {
		var guild = DiscordGateway.GetGuild(guildId);
		if (guild == null) return false;
		try {
			await guild.DownloadUsersAsync();
			return true;
		} catch (Exception e) {
			Logger.Error(typeof(Members), e, $"下载服务器成员失败：guild={guildId}");
			return false;
		}
	}

	public static IReadOnlyList<ulong> GetRoleMemberIds(ulong guildId, ulong roleId) {
		var role = DiscordGateway.GetGuild(guildId)?.GetRole(roleId);
		if (role is not SocketRole socketRole) return [];
		return socketRole.Members.Select(static member => member.Id).ToList();
	}
}
