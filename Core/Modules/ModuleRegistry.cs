using System.Collections.Concurrent;
using System.Reflection;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;

namespace Pengin1011.Core.Modules;

public static class ModuleRegistry {
	private static readonly ConcurrentDictionary<Assembly, LoadedModule> _runs = new();

	public static void Register(LoadedModule run) {
		if (_runs.TryGetValue(run.Assembly, out var existing) && !ReferenceEquals(existing, run)) {
			var message = Loc.Format("ModuleRunDuplicateRegistration", run.Name);
			Logger.Error(typeof(ModuleRegistry), message);
			throw new InvalidOperationException(message);
		}
		_runs[run.Assembly] = run;
	}

	public static LoadedModule? RunOf(Assembly assembly) {
		return _runs.TryGetValue(assembly, out var run) ? run : null;
	}

	internal static void ForgetAll() {
		_runs.Clear();
	}
}
