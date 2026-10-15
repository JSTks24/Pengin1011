
namespace Pengin1011.Services.AI;

public sealed class AIPresetData {
	public int MaxParallel { get; init; } = 5;

	public IReadOnlyList<AIPreset> Presets { get; init; } = [];
}

public sealed class AIPresetSnapshot {
	public static readonly AIPresetSnapshot Empty = new(0, new AIPresetData());

	internal AIPresetSnapshot(long version, AIPresetData data) {
		Version = version;
		MaxParallel = data.MaxParallel;
		Presets = [.. data.Presets];
	}

	public long Version { get; }

	public int MaxParallel { get; }

	public IReadOnlyList<AIPreset> Presets { get; }

	public AIPreset? Default => Presets.Count > 0 ? Presets[0] : null;

	public AIPreset? Find(string? name) {
		if (string.IsNullOrEmpty(name)) return Default;
		foreach (var preset in Presets) {
			if (string.Equals(preset.Name, name, StringComparison.Ordinal)) return preset;
		}
		return null;
	}
}
