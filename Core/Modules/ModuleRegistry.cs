using System.Collections.Concurrent;
using System.Reflection;
using QingQiu1011.Core.Logging;

namespace QingQiu1011.Core.Modules;

public static class ModuleRegistry {
	private static readonly ConcurrentDictionary<Assembly, LoadedModule> Runs = new();

	public static void Register(LoadedModule run) {
		if (Runs.TryGetValue(run.Assembly, out var existing) && !ReferenceEquals(existing, run)) {
			var message = $"模块运行记录重复注册：{run.Name}（旧运行对象仍存活，禁止覆盖）";
			Logger.Error(typeof(ModuleRegistry), message);
			throw new InvalidOperationException(message);
		}
		Runs[run.Assembly] = run;
	}

	public static LoadedModule? RunOf(Assembly assembly) {
		return Runs.TryGetValue(assembly, out var run) ? run : null;
	}

	internal static void ForgetAll() {
		Runs.Clear();
	}
}
