namespace Pengin1011.Services.AI;

public sealed class AIPresetUsageRegistrar {
	private readonly AIPresetUsageBatch _batch;

	internal AIPresetUsageRegistrar(AIPresetUsageBatch batch) {
		_batch = batch;
	}

	public void Add(string callSite, string presetName) {
		_batch.Add(callSite, string.IsNullOrEmpty(presetName) ? AIPresetDependency.Default : AIPresetDependency.Named(presetName));
	}

	public void AddDefault(string callSite) {
		_batch.Add(callSite, AIPresetDependency.Default);
	}

	public void AddNamed(string callSite, string presetName) {
		_batch.Add(callSite, AIPresetDependency.Named(presetName));
	}
}
