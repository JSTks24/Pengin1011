using System.Text.Json;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;
using Pengin1011.Helper;

namespace Pengin1011.Core;

public enum AIProvider {
	OpenAI,
	Gemini,
}

public sealed class DiscordConfig {
	public string Token { get; init; } = "";
}

public sealed class OpenAIConfig {
	public string ApiKey { get; init; } = "";
	public Uri? BaseUrl { get; init; }
	public string Model { get; init; } = "";

	public bool IsEmpty => ApiKey.Length == 0 && BaseUrl == null && Model.Length == 0;
}

public sealed class GeminiConfig {
	public string ApiKey { get; init; } = "";
	public string Project { get; init; } = "";
	public string Location { get; init; } = "";
	public string Model { get; init; } = "";

	public bool IsEmpty => ApiKey.Length == 0 && Project.Length == 0 && Location.Length == 0 && Model.Length == 0;
}

public sealed class AIConfig {
	public AIProvider Provider { get; init; } = AIProvider.OpenAI;
	public int MaxParallel { get; init; } = 5;
	public OpenAIConfig OpenAI { get; init; } = new();
	public GeminiConfig Gemini { get; init; } = new();
}

public static class AppConfig {
	private const string Template = """
		{
		  "Discord": {
		    "Token": ""
		  },
		  "AI": {
		    "Provider": "openai",
		    "MaxParallel": 5,
		    "OpenAI": {
		      "ApiKey": "",
		      "BaseUrl": "",
		      "Model": ""
		    },
		    "Gemini": {
		      "ApiKey": "",
		      "Project": "",
		      "Location": "",
		      "Model": ""
		    }
		  }
		}
		""";

	public static DiscordConfig Discord { get; private set; } = new();
	public static AIConfig AI { get; private set; } = new();

	public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "config.json");

	public static bool TryLoad(string? path, out IReadOnlyList<string> errors) {
		var list = new List<ConfigError>();
		var file = ReadFile(path ?? DefaultPath, list);
		DiscordConfig discord;
		AIConfig ai;
		if (file != null && TryBuild(file, list, out discord, out ai)) {
			Discord = discord;
			AI = ai;
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

	private static ConfigFile? ReadFile(string path, List<ConfigError> errors) {
		if (!File.Exists(path)) {
			try {
				if (JsonHelper.EnsureTemplateFile(path, Template)) {
					var created = Localizer.Format("ConfigCreatedFromTemplate", path);
					Logger.Info(typeof(AppConfig), created);
					errors.Add(new ConfigError(created));
				}
			} catch (Exception e) {
				errors.Add(new ConfigError(Localizer.Format("ConfigTemplateWriteFailed", path), e));
			}
			return null;
		}
		try {
			using var stream = File.OpenRead(path);
			var file = JsonSerializer.Deserialize<ConfigFile>(stream, JsonHelper.Options);
			if (file == null) {
				errors.Add(new ConfigError(Localizer.Format("ConfigRootMustBeObject", path)));
				return null;
			}
			return file;
		} catch (Exception e) {
			errors.Add(new ConfigError(Localizer.Format("ConfigReadFailed", path), e));
			return null;
		}
	}

	private static bool TryBuild(ConfigFile file, List<ConfigError> errors, out DiscordConfig discord, out AIConfig ai) {
		discord = new DiscordConfig();
		ai = new AIConfig();

		var token = (file.Discord?.Token ?? "").Trim();
		if (token.Length == 0) errors.Add(new ConfigError(Localizer.Get("DiscordTokenRequired")));

		var aiNode = file.AI;
		var providerText = (aiNode?.Provider ?? "").Trim().ToLowerInvariant();
		if (providerText.Length == 0) {
			errors.Add(new ConfigError(Localizer.Get("AIProviderRequired")));
		} else if (providerText != "openai" && providerText != "gemini") {
			errors.Add(new ConfigError(Localizer.Format("AIProviderInvalid", providerText)));
		}

		var maxParallel = aiNode?.MaxParallel ?? 5;
		if (maxParallel <= 0) errors.Add(new ConfigError(Localizer.Format("AIMaxParallelInvalid", maxParallel)));

		var openAINode = aiNode?.OpenAI;
		var openAIKey = (openAINode?.ApiKey ?? "").Trim();
		var openAIUrl = (openAINode?.BaseUrl ?? "").Trim();
		var openAIModel = (openAINode?.Model ?? "").Trim();
		var openAITouched = openAINode != null && (openAIKey.Length > 0 || openAIUrl.Length > 0 || openAIModel.Length > 0);
		if (openAITouched) {
			if (openAIKey.Length == 0) errors.Add(new ConfigError(Localizer.Get("OpenAIApiKeyRequired")));
			if (openAIModel.Length == 0) errors.Add(new ConfigError(Localizer.Get("OpenAIModelRequired")));
			if (openAIUrl.Length == 0) {
				errors.Add(new ConfigError(Localizer.Get("OpenAIBaseUrlRequired")));
			} else if (!Uri.TryCreate(openAIUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) {
				errors.Add(new ConfigError(Localizer.Format("OpenAIBaseUrlInvalid", openAIUrl)));
			}
		}
		if (providerText == "openai" && !openAITouched) errors.Add(new ConfigError(Localizer.Get("OpenAISectionRequired")));

		var geminiNode = aiNode?.Gemini;
		var geminiKey = (geminiNode?.ApiKey ?? "").Trim();
		var geminiProject = (geminiNode?.Project ?? "").Trim();
		var geminiLocation = (geminiNode?.Location ?? "").Trim();
		var geminiModel = (geminiNode?.Model ?? "").Trim();
		var geminiTouched = geminiNode != null && (geminiKey.Length > 0 || geminiProject.Length > 0 || geminiLocation.Length > 0 || geminiModel.Length > 0);
		if (geminiTouched) {
			if (geminiModel.Length == 0) errors.Add(new ConfigError(Localizer.Get("GeminiModelRequired")));
			var hasKey = geminiKey.Length > 0;
			var hasVertex = geminiProject.Length > 0 || geminiLocation.Length > 0;
			if (hasKey && hasVertex) {
				errors.Add(new ConfigError(Localizer.Get("GeminiAuthExclusive")));
			} else if (!hasKey && (geminiProject.Length == 0 || geminiLocation.Length == 0)) {
				errors.Add(new ConfigError(Localizer.Get("GeminiAuthIncomplete")));
			}
		}
		if (providerText == "gemini" && !geminiTouched) errors.Add(new ConfigError(Localizer.Get("GeminiSectionRequired")));

		if (errors.Count > 0) return false;

		discord = new DiscordConfig { Token = token };
		ai = new AIConfig {
			Provider = providerText == "gemini" ? AIProvider.Gemini : AIProvider.OpenAI,
			MaxParallel = maxParallel,
			OpenAI = new OpenAIConfig {
				ApiKey = openAIKey,
				BaseUrl = openAIUrl.Length > 0 ? new Uri(openAIUrl) : null,
				Model = openAIModel,
			},
			Gemini = new GeminiConfig {
				ApiKey = geminiKey,
				Project = geminiProject,
				Location = geminiLocation,
				Model = geminiModel,
			},
		};
		return true;
	}

	private sealed class ConfigFile {
		public ConfigDiscord? Discord { get; set; }
		public ConfigAI? AI { get; set; }
	}

	private sealed class ConfigDiscord {
		public string? Token { get; set; }
	}

	private sealed class ConfigAI {
		public string? Provider { get; set; }
		public int? MaxParallel { get; set; }
		public ConfigOpenAI? OpenAI { get; set; }
		public ConfigGemini? Gemini { get; set; }
	}

	private sealed class ConfigOpenAI {
		public string? ApiKey { get; set; }
		public string? BaseUrl { get; set; }
		public string? Model { get; set; }
	}

	private sealed class ConfigGemini {
		public string? ApiKey { get; set; }
		public string? Project { get; set; }
		public string? Location { get; set; }
		public string? Model { get; set; }
	}
}

internal sealed record ConfigError(string Message, Exception? Exception = null);
