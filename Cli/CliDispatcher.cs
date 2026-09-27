using QingQiu1011.Core;
using QingQiu1011.Services.AI;
using QingQiu1011.Services.Discord;

namespace QingQiu1011;

public static class CliDispatcher {
	private const string ModuleReloadRemoved = "热重载已移除：模块属于当前进程，改动模块后请 exit 退出进程，确认进程结束后替换产物，再用原命令启动";

	private const string HelpText = """
		help                 显示本帮助
		status               框架运行概况
		modules              列出已加载模块
		db backup            立即执行全库备份
		db status            查看各数据库状态
		sync                 重新注册当前进程已加载的 Discord 命令树
		exit                 退出框架
		""";

	public static DateTimeOffset StartedAt { get; internal set; } = DateTimeOffset.Now;

	public static async Task<CliResult> ExecuteAsync(string line) {
		var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0) return new CliResult("", false);
		var command = parts[0].ToLowerInvariant();
		switch (command) {
			case "help":
				return new CliResult(HelpText, false);
			case "status":
				return new CliResult(StatusText(), false);
			case "modules":
				return new CliResult(ModulesText(), false);
			case "reload":
				return new CliResult(ModuleReloadRemoved, false);
			case "db":
				return new CliResult(await DbAsync(parts), false);
			case "sync": {
					var error = await InteractionHost.SyncAsync(InteractionHost.ShutdownToken);
					return new CliResult(error == null ? "命令树已重新注册" : $"注册失败：{error}", false);
				}
			case "exit":
			case "quit":
				return new CliResult("正在退出……", true);
			default:
				return new CliResult($"未知命令：{command}（输入 help 查看命令列表）", false);
		}
	}

	private static string StatusText() {
		var uptime = DateTimeOffset.Now - StartedAt;
		var model = AppConfig.AI.Provider == AIProvider.OpenAI ? AppConfig.AI.OpenAI.Model : AppConfig.AI.Gemini.Model;
		return $"运行时长：{uptime:dd\\.hh\\:mm\\:ss}（启动于 {StartedAt:yyyy-MM-dd HH:mm:ss}）\n" +
			$"网关状态：{DiscordGateway.ConnectionState}\n" +
			$"AI：{AppConfig.AI.Provider} / {model}\n" +
			$"模块：{ModuleHost.Modules.Count} 个\n" +
			$"数据库：{Databases.GetStatus().Count} 个";
	}

	private static string ModulesText() {
		var modules = ModuleHost.Modules;
		if (modules.Count == 0) return "当前没有已加载的模块";
		return string.Join("\n", modules.Select(module => $"{module.Name}（{module.State}，{Path.GetFileName(module.FilePath)}，加载于 {module.LoadedAt:HH:mm:ss}）"));
	}

	private static async Task<string> DbAsync(string[] parts) {
		if (parts.Length >= 2 && parts[1].ToLowerInvariant() == "backup") {
			var result = await Databases.BackupNowAsync();
			if (!result.Success) {
				return $"备份完成（成功 {result.Succeeded}/{result.Total} 库）：\n{string.Join("\n", result.Failures.Select(failure => $"  {failure.File}: {failure.Reason}"))}";
			}
			return $"全库备份完成（成功 {result.Succeeded}/{result.Total} 库）";
		}
		if (parts.Length >= 2 && parts[1].ToLowerInvariant() == "status") {
			var statuses = Databases.GetStatus();
			if (statuses.Count == 0) return "当前没有任何数据库";
			return string.Join("\n", statuses.Select(status => {
				var size = status.SizeBytes < 1024 ? $"{status.SizeBytes} B" : $"{status.SizeBytes / 1024.0:F1} KB";
				return $"{status.Module}：{size}，迁移 {status.MigrationId ?? "无记录"}，最近备份 {status.LastBackup?.ToString("MM-dd HH:mm") ?? "无"}";
			}));
		}
		return "用法：db backup / db status";
	}
}

public sealed record CliResult(string Output, bool ShouldExit);
