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

	[SlashCommand("verify", "校验数据库或备份")]
	public Task Verify(string database, string? backup = null, bool confirm = false) {
		return ReplyAsync(backup != null ? ["db", "verify", database, backup]
			: confirm ? ["db", "verify", database, "--confirm", "--accept-interrupted"] : ["db", "verify", database]);
	}

	[SlashCommand("restore", "从备份恢复，完成后重启")]
	public Task Restore(string database, string backup, bool confirm = false) {
		return ReplyAsync(confirm ? ["db", "restore", database, backup, "--confirm"] : ["db", "restore", database, backup]);
	}

	[SlashCommand("recover", "完成或回滚中断的恢复，完成后重启")]
	public Task Recover(string database, string operation, bool rollback = false, bool confirm = false) {
		var arguments = new List<string> { "db", "recover", database, operation, rollback ? "--rollback" : "--complete" };
		if (confirm) arguments.Add("--confirm");
		return ReplyAsync(arguments);
	}

	[SlashCommand("unlock", "清除已停止数据库的迁移残留锁")]
	public Task Unlock(string database, bool confirm = false) {
		return ReplyAsync(confirm ? ["db", "unlock", database, "--confirm"] : ["db", "unlock", database]);
	}

	private async Task ReplyAsync(IReadOnlyList<string> arguments) {
		await DeferAsync(ephemeral: true);
		var output = await DatabaseCommands.ExecuteAsync(arguments, allowFileImport: false);
		foreach (var chunk in MessageSplitter.Split(output)) {
			await FollowupAsync(chunk, ephemeral: true, allowedMentions: Discord.AllowedMentions.None);
		}
	}
}
