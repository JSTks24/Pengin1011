using Discord.Interactions;
using Pengin1011.Core;
using Pengin1011.Core.Modules;
using Pengin1011.Services.Discord;

namespace Pengin1011.Modules.AIPresetAdmin;

[ModuleAvailability(typeof(AIPresetAdminRuntime))]
[RequireBotAdmin]
[Group("db", "数据库管理")]
public sealed class DatabaseAdmin : InteractionModuleBase<SocketInteractionContext> {
	[SlashCommand("status", "查看数据库状态")]
	public Task Status(string? database = null) => ReplyAsync(database == null ? ["db", "status"] : ["db", "status", database]);

	[SlashCommand("backups", "列出正式备份")]
	public Task Backups(string database) => ReplyAsync(["db", "backups", database]);

	[SlashCommand("backup", "立即备份数据库")]
	public Task Backup() => ReplyAsync(["db", "backup"]);

	[SlashCommand("verify", "校验指定备份")]
	public Task Verify(string database, string backup) => ReplyAsync(["db", "verify", database, backup]);

	private async Task ReplyAsync(IReadOnlyList<string> arguments) {
		await DeferAsync(ephemeral: true);
		var output = await DatabaseCommands.ExecuteAsync(arguments);
		foreach (var chunk in MessageSplitter.Split(output)) {
			await FollowupAsync(chunk, ephemeral: true, allowedMentions: Discord.AllowedMentions.None);
		}
	}
}
