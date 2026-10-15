using Pengin1011.Core;
using Pengin1011.Services.AI;

namespace Pengin1011.Modules.AIPresetAdmin;

public static class AIPresetAdminLogic {
	public static readonly string[] Fields = PresetEdit.Fields;

	public static string List() {
		var presets = AIPresetStore.Presets;
		if (presets.Count == 0) return Loc.Get("PresetReplyEmpty");
		var defaultName = AIPresetStore.DefaultPreset?.Name;
		return string.Join("\n", presets.Select(preset => Loc.Format(
			"PresetReplyListEntry",
			preset.Name,
			preset.Provider,
			preset.Model,
			preset.Name == defaultName ? Loc.Get("PresetReplyDefaultSuffix") : "")));
	}

	public static string Show(string name) {
		var preset = AIPresetStore.Find(name);
		if (preset == null) return Loc.Format("PresetReplyNotFound", name);
		var number = System.Globalization.CultureInfo.InvariantCulture;
		return string.Join("\n", new[] {
			Loc.Format("PresetReplyField", "name", preset.Name),
			Loc.Format("PresetReplyField", "provider", preset.Provider),
			Loc.Format("PresetReplyField", "apikey", Secret.Reveal(preset.ApiKey)),
			Loc.Format("PresetReplyField", "baseurl", Field(preset.BaseUrl)),
			Loc.Format("PresetReplyField", "project", Field(preset.Project)),
			Loc.Format("PresetReplyField", "location", Field(preset.Location)),
			Loc.Format("PresetReplyField", "model", Field(preset.Model)),
			Loc.Format("PresetReplyField", "temperature", preset.Temperature?.ToString(number) ?? "-"),
			Loc.Format("PresetReplyField", "topp", preset.TopP?.ToString(number) ?? "-"),
			Loc.Format("PresetReplyField", "maxoutputtokens", preset.MaxOutputTokens?.ToString(number) ?? "-"),
		});
	}

	public static string Add(string name, string providerText, AIPresetAddFields fields) {
		if (!PresetEdit.TryProvider(providerText, out var provider)) return Loc.Format("PresetReplyProviderInvalid", providerText);
		var candidate = new AIPreset {
			Name = name,
			Provider = provider,
			ApiKey = fields.ApiKey ?? "",
			BaseUrl = fields.BaseUrl ?? "",
			Project = fields.Project ?? "",
			Location = fields.Location ?? "",
			Model = fields.Model ?? "",
		};
		if (!AIPresetStore.AddPatch(candidate, null, out var result)) return Describe(result);
		return Loc.Format("PresetReplyAdded", candidate.Name);
	}

	public static string Set(string name, string field, string value) {
		var existing = AIPresetStore.Find(name);
		if (existing == null) return Loc.Format("PresetReplyNotFound", name);
		if (!PresetEdit.TryBuildPatch([$"{field}={value}"], allowName: true, out var patch, out var failure)) return Describe(failure);
		if (!AIPresetStore.Apply(name, patch, null, out var result)) return Describe(result);
		var renamed = patch.Name.TryGet(out var newName) ? newName : name;
		return Loc.Format("PresetReplyUpdated", renamed);
	}

	public static string SetMany(string name, IReadOnlyList<KeyValuePair<string, string>> values) {
		var existing = AIPresetStore.Find(name);
		if (existing == null) return Loc.Format("PresetReplyNotFound", name);
		if (!PresetEdit.TryBuildPatch(values.Select(pair => $"{pair.Key}={pair.Value}"), allowName: true, out var patch, out var failure)) return Describe(failure);
		if (!AIPresetStore.Apply(name, patch, null, out var result)) return Describe(result);
		var renamed = patch.Name.TryGet(out var newName) ? newName : name;
		return Loc.Format("PresetReplyUpdated", renamed);
	}

	public static string Remove(string name) {
		if (!AIPresetStore.RemovePatch(name, null, out var result)) return Describe(result);
		return Loc.Format("PresetReplyRemoved", name);
	}

	private static string Describe(PresetEditFailure failure) {
		return failure.Kind switch {
			PresetEditFailureKind.FieldMalformed => Loc.Format("PresetReplyFieldInvalid", failure.Value),
			PresetEditFailureKind.FieldUnknown => Loc.Format("PresetReplyFieldUnknown", failure.Value),
			PresetEditFailureKind.ProviderInvalid => Loc.Format("PresetReplyProviderInvalid", failure.Value),
			PresetEditFailureKind.NameRequired => Loc.Get("AIPresetNameRequired"),
			_ => Loc.Format("PresetReplyValueInvalid", failure.Value),
		};
	}

	private static string Describe(AIPresetEditResult result) {
		return result.Error ?? Loc.Get("PresetReplySaveFailed");
	}

	private static string Field(string value) {
		return value.Length > 0 ? value : "-";
	}
}

public sealed record AIPresetAddFields(string? ApiKey = null, string? BaseUrl = null, string? Project = null, string? Location = null, string? Model = null);
