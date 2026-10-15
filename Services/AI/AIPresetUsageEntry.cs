namespace Pengin1011.Services.AI;

public sealed record AIPresetUsageEntry(string Owner, string CallSite, AIPresetDependency Dependency);

public sealed class AIPresetUsageBatch {
	private readonly List<AIPresetUsageEntry> _entries = [];

	internal AIPresetUsageBatch(string token, string owner) {
		Token = token;
		Owner = owner;
	}

	internal string Token { get; }

	internal string Owner { get; }

	internal IReadOnlyList<AIPresetUsageEntry> Entries => _entries;

	internal void Add(string callSite, AIPresetDependency dependency) {
		_entries.Add(new AIPresetUsageEntry(Owner, callSite, dependency));
	}
}
