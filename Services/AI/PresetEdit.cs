using Pengin1011.Core;

namespace Pengin1011.Services.AI;

public enum PresetEditFailureKind {
	None,
	FieldMalformed,
	FieldUnknown,
	ProviderInvalid,
	ValueInvalid,
	NameRequired,
}

public readonly struct PresetEditFailure {
	public PresetEditFailure(PresetEditFailureKind kind, string value) {
		Kind = kind;
		Value = value;
	}

	public PresetEditFailureKind Kind { get; }

	public string Value { get; }

	public bool IsNone => Kind == PresetEditFailureKind.None;
}

public static class PresetEdit {
	public static readonly string[] Fields = ["name", "provider", "apikey", "baseurl", "project", "location", "model", "temperature", "topp", "maxoutputtokens"];

	public static bool TryProvider(string text, out AIPresetProvider provider) {
		if (Enum.TryParse<AIPresetProvider>(text, ignoreCase: true, out provider) && Enum.IsDefined(provider)) return true;
		provider = AIPresetProvider.OpenAI;
		return false;
	}

	public static bool TryBuildPatch(IEnumerable<string> pairs, bool allowName, out AIPresetPatch patch, out PresetEditFailure failure) {
		var builder = new AIPresetPatchBuilder();
		failure = default;
		foreach (var pair in pairs) {
			if (!TrySplit(pair, out var key, out var value)) {
				patch = builder.Build();
				failure = new PresetEditFailure(PresetEditFailureKind.FieldMalformed, pair);
				return false;
			}
			var field = key.ToLowerInvariant();
			if (Array.IndexOf(Fields, field) < 0 || (field == "name" && !allowName)) {
				patch = builder.Build();
				failure = new PresetEditFailure(PresetEditFailureKind.FieldUnknown, key);
				return false;
			}
			if (!builder.Apply(field, value, out failure)) {
				patch = builder.Build();
				return false;
			}
		}
		patch = builder.Build();
		return true;
	}

	public static string Describe(AIPresetEditResult result) {
		return result.Error ?? Loc.Get("AIPresetsSaveFailed");
	}

	private static bool TrySplit(string pair, out string key, out string value) {
		var index = pair.IndexOf('=');
		if (index <= 0) {
			key = "";
			value = "";
			return false;
		}
		key = pair[..index];
		value = pair[(index + 1)..];
		return true;
	}
}
