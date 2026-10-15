namespace Pengin1011.Services.AI;

public readonly struct AIPresetDependency : IEquatable<AIPresetDependency> {
	private readonly string? _name;

	private AIPresetDependency(string? name) {
		_name = name;
	}

	public static AIPresetDependency Default => default;

	public static AIPresetDependency Named(string name) {
		return string.IsNullOrEmpty(name) ? default : new AIPresetDependency(name);
	}

	public bool IsDefault => _name is null;

	public string Name => _name ?? "";

	public bool Matches(IReadOnlyList<AIPreset> presets) {
		if (IsDefault) return presets.Count > 0;
		foreach (var preset in presets) {
			if (string.Equals(preset.Name, _name, StringComparison.Ordinal)) return true;
		}
		return false;
	}

	public string Describe() {
		return IsDefault ? "" : _name!;
	}

	public bool Equals(AIPresetDependency other) {
		return IsDefault && other.IsDefault || string.Equals(_name, other._name, StringComparison.Ordinal);
	}

	public override bool Equals(object? obj) {
		return obj is AIPresetDependency other && Equals(other);
	}

	public override int GetHashCode() {
		return _name is null ? 0 : StringComparer.Ordinal.GetHashCode(_name);
	}

	public override string ToString() {
		return IsDefault ? "<default>" : _name!;
	}
}
