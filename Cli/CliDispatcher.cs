using Pengin1011.Core;
using Pengin1011.Core.Localization;
using Pengin1011.Services.AI;
using Pengin1011.Services.Discord;

namespace Pengin1011;

public static class CliDispatcher {
	public static DateTimeOffset StartedAt { get; internal set; } = DateTimeOffset.Now;

	public static async Task<CliResult> ExecuteAsync(string line) {
		var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0) return new CliResult("", false);
		var command = parts[0].ToLowerInvariant();
		switch (command) {
			case "help":
				return new CliResult(Localizer.Get("CliHelp"), false);
			case "status":
				return new CliResult(StatusText(), false);
			case "modules":
				return new CliResult(ModulesText(), false);
			case "reload":
				return new CliResult(Localizer.Get("ReloadRemoved"), false);
			case "db":
				return new CliResult(await DbAsync(parts), false);
			case "sync": {
					var error = await InteractionHost.SyncAsync(InteractionHost.ShutdownToken);
					return new CliResult(error == null ? Localizer.Get("CommandTreeSynced") : Localizer.Format("CommandTreeSyncFailed", error), false);
				}
			case "exit":
			case "quit":
				return new CliResult(Localizer.Get("ExitingNow"), true);
			default:
				return new CliResult(Localizer.Format("UnknownCommand", command), false);
		}
	}

	private static string StatusText() {
		var uptime = DateTimeOffset.Now - StartedAt;
		var model = AppConfig.AI.Provider == AIProvider.OpenAI ? AppConfig.AI.OpenAI.Model : AppConfig.AI.Gemini.Model;
		return Localizer.Format(
			"CliStatus",
			uptime,
			StartedAt,
			DiscordGateway.ConnectionState,
			AppConfig.AI.Provider,
			model,
			ModuleHost.Modules.Count,
			Databases.GetStatus().Count);
	}

	private static string ModulesText() {
		var modules = ModuleHost.Modules;
		if (modules.Count == 0) return Localizer.Get("NoModulesLoaded");
		return string.Join("\n", modules.Select(module => Localizer.Format("ModuleListEntry", module.Name, module.State, Path.GetFileName(module.FilePath), module.LoadedAt)));
	}

	private static async Task<string> DbAsync(string[] parts) {
		if (parts.Length >= 2 && parts[1].ToLowerInvariant() == "backup") {
			var result = await Databases.BackupNowAsync();
			if (!result.Success) {
				return Localizer.Format("DbBackupPartial", result.Succeeded, result.Total, string.Join("\n", result.Failures.Select(failure => $"  {failure.File}: {failure.Reason}")));
			}
			return Localizer.Format("DbBackupCompleted", result.Succeeded, result.Total);
		}
		if (parts.Length >= 2 && parts[1].ToLowerInvariant() == "status") {
			var statuses = Databases.GetStatus();
			if (statuses.Count == 0) return Localizer.Get("NoDatabases");
			return string.Join("\n", statuses.Select(status => {
				var size = status.SizeBytes < 1024 ? $"{status.SizeBytes} B" : $"{status.SizeBytes / 1024.0:F1} KB";
				var migration = status.MigrationId ?? Localizer.Get("DbStatusNoMigration");
				var lastBackup = status.LastBackup?.ToString("MM-dd HH:mm") ?? Localizer.Get("DbStatusNoBackup");
				return Localizer.Format("DbStatusLine", status.Module, size, migration, lastBackup);
			}));
		}
		return Localizer.Get("DbUsage");
	}
}

public sealed record CliResult(string Output, bool ShouldExit);
