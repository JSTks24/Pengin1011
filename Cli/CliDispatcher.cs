using Pengin1011.Core;
using Pengin1011.Core.Localization;
using Pengin1011.Services.AI;
using Pengin1011.Services.Discord;

namespace Pengin1011;

public static class CliDispatcher {
	public static DateTimeOffset StartedAt { get; internal set; } = DateTimeOffset.Now;

	public static async Task<CliResult> ExecuteAsync(string line) {
		var parts = Tokenize(line);
		if (parts.Count == 0) return new CliResult("", false);
		var command = parts[0].ToLowerInvariant();
		switch (command) {
			case "help":
				return new CliResult(Loc.Get("CliHelp"), false);
			case "status":
				return new CliResult(StatusText(), false);
			case "modules":
				return new CliResult(ModulesText(), false);
			case "reload":
				return new CliResult(Loc.Get("ReloadRemoved"), false);
			case "db":
				return new CliResult(await DatabaseCommands.ExecuteAsync(parts), false);
			case "preset":
				if (HostMaintenance.MaintenanceOnly) return new CliResult(Loc.Get("MaintenanceConsole"), false);
				return new CliResult(Preset(parts), false);
			case "sync": {
					if (HostMaintenance.MaintenanceOnly) return new CliResult(Loc.Get("MaintenanceConsole"), false);
					var error = await InteractionHost.SyncAsync(InteractionHost.ShutdownToken);
					return new CliResult(error == null ? Loc.Get("CommandTreeSynced") : Loc.Format("CommandTreeSyncFailed", error), false);
				}
			case "exit":
			case "quit":
				return new CliResult(Loc.Get("ExitingNow"), true);
			default:
				return new CliResult(Loc.Format("UnknownCommand", command), false);
		}
	}

	internal static IReadOnlyList<string> Tokenize(string line) {
		var tokens = new List<string>();
		var current = new System.Text.StringBuilder();
		var quoted = false;
		var tokenStarted = false;
		foreach (var character in line.Trim()) {
			if (character == '"') {
				quoted = !quoted;
				tokenStarted = true;
				continue;
			}
			if (!quoted && char.IsWhiteSpace(character)) {
				if (tokenStarted) {
					tokens.Add(current.ToString());
					current.Clear();
					tokenStarted = false;
				}
				continue;
			}
			current.Append(character);
			tokenStarted = true;
		}
		if (tokenStarted) tokens.Add(current.ToString());
		return tokens;
	}

	private static string StatusText() {
		var uptime = DateTimeOffset.Now - StartedAt;
		var presets = AIPresetStore.Presets;
		var defaultName = AIPresetStore.DefaultPreset?.Name ?? Loc.Get("AIModelNotConfigured");
		var summary = presets.Count == 0
			? Loc.Get("CliPresetNone")
			: string.Join("\n", presets.Select(preset => Loc.Format("CliPresetListEntry", preset.Name, preset.Provider, preset.Model, preset == AIPresetStore.DefaultPreset ? Loc.Get("CliPresetDefaultSuffix") : "")));
		return Loc.Format(
			"CliStatus",
			uptime,
			StartedAt,
			DiscordGateway.ConnectionState,
			presets.Count,
			defaultName,
			ModuleHost.Modules.Count,
			Databases.GetStatus().Count,
			summary);
	}

	private static string ModulesText() {
		var modules = ModuleHost.Modules;
		if (modules.Count == 0) return Loc.Get("NoModulesLoaded");
		return string.Join("\n", modules.Select(module => Loc.Format("ModuleListEntry", module.Name, module.State, Path.GetFileName(module.FilePath), module.LoadedAt)));
	}

	private static string Preset(IReadOnlyList<string> parts) {
		if (parts.Count < 2) return Loc.Get("CliPresetUsage");
		var sub = parts[1].ToLowerInvariant();
		return sub switch {
			"list" => PresetList(),
			"show" => parts.Count >= 3 ? PresetShow(parts[2]) : Loc.Get("CliPresetUsage"),
			"add" => parts.Count >= 4 ? PresetAdd(parts[2], parts[3], parts.Skip(4)) : Loc.Get("CliPresetUsage"),
			"set" => parts.Count >= 4 ? PresetSet(parts[2], parts.Skip(3)) : Loc.Get("CliPresetUsage"),
			"remove" => parts.Count >= 3 ? PresetRemove(parts[2]) : Loc.Get("CliPresetUsage"),
			_ => Loc.Get("CliPresetUsage"),
		};
	}

	private static string PresetList() {
		var presets = AIPresetStore.Presets;
		if (presets.Count == 0) return Loc.Get("CliPresetNone");
		var defaultName = AIPresetStore.DefaultPreset?.Name;
		return string.Join("\n", presets.Select(preset => Loc.Format(
			"CliPresetListEntry",
			preset.Name,
			preset.Provider,
			preset.Model,
			preset.Name == defaultName ? Loc.Get("CliPresetDefaultSuffix") : "")));
	}

	private static string PresetShow(string name) {
		var preset = AIPresetStore.Find(name);
		if (preset == null) return Loc.Format("AIPresetNotFound", name);
		var number = System.Globalization.CultureInfo.InvariantCulture;
		return string.Join("\n", new[] {
			Loc.Format("CliPresetShowLine", "name", preset.Name),
			Loc.Format("CliPresetShowLine", "provider", preset.Provider),
			Loc.Format("CliPresetShowLine", "apikey", Secret.Reveal(preset.ApiKey)),
			Loc.Format("CliPresetShowLine", "baseurl", Field(preset.BaseUrl)),
			Loc.Format("CliPresetShowLine", "project", Field(preset.Project)),
			Loc.Format("CliPresetShowLine", "location", Field(preset.Location)),
			Loc.Format("CliPresetShowLine", "model", Field(preset.Model)),
			Loc.Format("CliPresetShowLine", "temperature", preset.Temperature?.ToString(number) ?? "-"),
			Loc.Format("CliPresetShowLine", "topp", preset.TopP?.ToString(number) ?? "-"),
			Loc.Format("CliPresetShowLine", "maxoutputtokens", preset.MaxOutputTokens?.ToString(number) ?? "-"),
		});
	}

	private static string PresetAdd(string name, string providerText, IEnumerable<string> pairs) {
		if (!TryProvider(providerText, out var provider)) return Loc.Format("CliPresetProviderInvalid", providerText);
		if (!PresetEdit.TryBuildPatch(pairs, allowName: false, out var patch, out var failure)) return Describe(failure);
		var candidate = patch.Apply(new AIPreset { Name = name, Provider = provider });
		if (!AIPresetStore.AddPatch(candidate, null, out var result)) return PresetEdit.Describe(result);
		return Loc.Format("CliPresetAdded", candidate.Name);
	}

	private static string PresetSet(string name, IEnumerable<string> pairs) {
		var existing = AIPresetStore.Find(name);
		if (existing == null) return Loc.Format("AIPresetNotFound", name);
		if (!PresetEdit.TryBuildPatch(pairs, allowName: true, out var patch, out var failure)) return Describe(failure);
		if (patch.IsEmpty) return Loc.Get("CliPresetUsage");
		if (!AIPresetStore.Apply(name, patch, null, out var result)) return PresetEdit.Describe(result);
		var renamed = patch.Name.TryGet(out var newName) ? newName : name;
		return Loc.Format("CliPresetUpdated", renamed);
	}

	private static string PresetRemove(string name) {
		if (!AIPresetStore.RemovePatch(name, null, out var result)) return PresetEdit.Describe(result);
		return Loc.Format("CliPresetRemoved", name);
	}

	private static string Describe(PresetEditFailure failure) {
		return failure.Kind switch {
			PresetEditFailureKind.FieldMalformed => Loc.Format("CliPresetFieldInvalid", failure.Value),
			PresetEditFailureKind.FieldUnknown => Loc.Format("CliPresetFieldUnknown", failure.Value),
			PresetEditFailureKind.ProviderInvalid => Loc.Format("CliPresetProviderInvalid", failure.Value),
			PresetEditFailureKind.NameRequired => Loc.Get("AIPresetNameRequired"),
			_ => Loc.Format("CliPresetFieldInvalid", failure.Value),
		};
	}

	private static string Field(string value) {
		return value.Length > 0 ? value : "-";
	}

	private static bool TryProvider(string text, out AIPresetProvider provider) {
		return PresetEdit.TryProvider(text, out provider);
	}
}

public sealed record CliResult(string Output, bool ShouldExit);
