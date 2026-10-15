using Pengin1011.Core;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;

namespace Pengin1011.Services.AI;

public static class AIPresetValidator {
	public static IReadOnlyList<string> CollectMissing(IReadOnlyList<AIPreset> presets, IReadOnlyList<AIPresetUsageEntry> usages) {
		var failures = new List<string>();
		foreach (var usage in usages) {
			if (usage.Dependency.IsDefault) {
				if (presets.Count == 0) failures.Add(Loc.Format("AIPresetUsageDefaultMissing", usage.Owner, usage.CallSite));
				continue;
			}
			if (!Exists(presets, usage.Dependency.Name)) failures.Add(Loc.Format("AIPresetUsageMissing", usage.Owner, usage.CallSite, usage.Dependency.Name));
		}
		return failures;
	}

	public static bool TryValidate(out IReadOnlyList<string> failures) {
		failures = CollectMissing(AIPresetStore.Presets, AIPresetUsageRegistry.Usages);
		foreach (var failure in failures) {
			Logger.Error(typeof(AIPresetValidator), failure);
		}
		return failures.Count == 0;
	}

	private static bool Exists(IReadOnlyList<AIPreset> presets, string name) {
		foreach (var preset in presets) {
			if (string.Equals(preset.Name, name, StringComparison.Ordinal)) return true;
		}
		return false;
	}
}
