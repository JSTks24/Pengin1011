using System.Text.Json;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;
using Pengin1011.Helper;

namespace Pengin1011.Core;

public sealed class DiscordConfig {
	public string Token { get; init; } = "";
	public IReadOnlyList<ulong> AdminRoleIds { get; init; } = [];
}

public sealed class LoggerConfig {
	public IReadOnlyList<ulong> Channels { get; init; } = [];
}

public sealed class DatabaseConfig {
	public string? DataDirectory { get; init; }
}

public static class AppConfig {
	private const string Template = """
		{
		  "Discord": {
		    "Token": "",
		    "AdminRoleIds": []
		  },
		  "Databases": {
		    "DataDirectory": null
		  },
		  "Logger": {
		    "Channels": []
		  }
		}
		""";

	public static DiscordConfig Discord { get; private set; } = new();
	public static LoggerConfig Logging { get; private set; } = new();

	internal static bool PathConfigurationValid { get; private set; }

	public static DatabaseConfig Databases { get; private set; } = new();

	public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "config.json");

	public static bool TryLoad(string? path, out IReadOnlyList<string> errors) {
		var list = new List<ConfigError>();
		var configPath = path ?? DefaultPath;
		var file = ReadFile(configPath, list, out var templateCreated);
		PathConfigurationValid = file != null || templateCreated;
		Databases = new DatabaseConfig { DataDirectory = file?.Databases?.DataDirectory };
		DiscordConfig discord;
		LoggerConfig logger;
		if (file != null && TryBuild(file, list, out discord, out logger)) {
			Discord = discord;
			Logging = logger;
		}
		errors = [.. list.Select(entry => entry.Message)];
		foreach (var entry in list) {
			if (entry.Exception != null) {
				Logger.Error(typeof(AppConfig), entry.Exception, entry.Message);
			} else {
				Logger.Error(typeof(AppConfig), entry.Message);
			}
		}
		return list.Count == 0;
	}

	private static ConfigFile? ReadFile(string path, List<ConfigError> errors, out bool templateCreated) {
		templateCreated = false;
		if (!File.Exists(path)) {
			try {
				if (JsonHelper.EnsureTemplateFile(path, Template)) {
					templateCreated = true;
					var created = Loc.Format("ConfigCreatedFromTemplate", path);
					Logger.Info(typeof(AppConfig), created);
					errors.Add(new ConfigError(created));
				}
			} catch (Exception e) {
				errors.Add(new ConfigError(Loc.Format("ConfigTemplateWriteFailed", path), e));
			}
			return null;
		}
		try {
			using var stream = File.OpenRead(path);
			var file = JsonSerializer.Deserialize<ConfigFile>(stream, JsonHelper.Options);
			if (file == null) {
				errors.Add(new ConfigError(Loc.Format("ConfigRootMustBeObject", path)));
				return null;
			}
			return file;
		} catch (Exception e) {
			errors.Add(new ConfigError(Loc.Format("ConfigReadFailed", path), e));
			return null;
		}
	}

	private static bool TryBuild(ConfigFile file, List<ConfigError> errors, out DiscordConfig discord, out LoggerConfig logger) {
		discord = new DiscordConfig();
		logger = new LoggerConfig();

		var token = (file.Discord?.Token ?? "").Trim();
		if (token.Length == 0) errors.Add(new ConfigError(Loc.Get("DiscordTokenRequired")));

		var adminRoles = new List<ulong>();
		foreach (var raw in file.Discord?.AdminRoleIds ?? []) {
			if (!ulong.TryParse(raw, out var roleId) || roleId == 0) errors.Add(new ConfigError(Loc.Format("AdminRoleInvalid", raw)));
			else if (!adminRoles.Contains(roleId)) adminRoles.Add(roleId);
		}
		var loggerChannels = new List<ulong>();
		foreach (var raw in file.Logger?.Channels ?? []) {
			var text = (raw ?? "").Trim();
			if (!ulong.TryParse(text, out var channelId) || channelId == 0) {
				errors.Add(new ConfigError(Loc.Format("LoggerChannelInvalid", text)));
				continue;
			}
			loggerChannels.Add(channelId);
		}

		if (errors.Count > 0) return false;

		discord = new DiscordConfig { Token = token, AdminRoleIds = adminRoles };
		logger = new LoggerConfig { Channels = loggerChannels };
		return true;
	}

	private sealed class ConfigFile {
		public ConfigDiscord? Discord { get; set; }
		public ConfigLogger? Logger { get; set; }
		public DatabaseConfig? Databases { get; set; }
	}

	private sealed class ConfigDiscord {
		public string? Token { get; set; }
		public List<string>? AdminRoleIds { get; set; }
	}

	private sealed class ConfigLogger {
		public List<string>? Channels { get; set; }
	}
}

internal sealed record ConfigError(string Message, Exception? Exception = null);
