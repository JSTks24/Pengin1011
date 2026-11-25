namespace Pengin1011.Services.AI;

public sealed class AIPresetDocument {
	public int MaxParallel { get; init; } = 5;
	public IReadOnlyList<AIPreset> Presets { get; init; } = [];
}
