using System.Reflection;
using System.Runtime.Loader;
using QingQiu1011.Core.Logging;
using QingQiu1011.Core.Modules;
using QingQiu1011.Services.Discord;

namespace QingQiu1011.Core;

public static class ModuleHost {
	internal static TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
	internal static TimeSpan CleanupBudget = TimeSpan.FromSeconds(10);

	private static readonly object Gate = new();
	private static readonly List<LoadedModule> Loaded = [];
	private static readonly Dictionary<string, Assembly> Assemblies = new(StringComparer.OrdinalIgnoreCase);
	private static bool _scanned;
	internal static string? ModuleDirectoryOverrideForTest;

	public static string ModuleDirectory => ModuleDirectoryOverrideForTest ?? Path.Combine(AppContext.BaseDirectory, "module");

	public static IReadOnlyList<LoadedModule> Modules {
		get {
			lock (Gate) {
				return [.. Loaded];
			}
		}
	}

	public static LoadedModule? Find(string name) {
		lock (Gate) {
			return Loaded.FirstOrDefault(module => string.Equals(module.Name, name, StringComparison.OrdinalIgnoreCase));
		}
	}

	public static void LoadAll() {
		lock (Gate) {
			if (_scanned) {
				Logger.Info(typeof(ModuleHost), "模块目录已完成扫描，本次启动不再装载新模块");
				return;
			}
			_scanned = true;
		}
		if (!Directory.Exists(ModuleDirectory)) {
			Logger.Info(typeof(ModuleHost), $"模块目录不存在（{ModuleDirectory}），框架以零模块运行");
			return;
		}
		var files = Directory.EnumerateFiles(ModuleDirectory, "*.dll", SearchOption.TopDirectoryOnly)
			.OrderBy(file => file, StringComparer.Ordinal)
			.ToList();
		foreach (var file in files) {
			var name = Path.GetFileNameWithoutExtension(file);
			lock (Gate) {
				if (Loaded.Any(module => module.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
			}
			try {
				Adopt(LoadOne(name, file));
			} catch (Exception e) {
				Logger.Error(typeof(ModuleHost), e, $"模块加载失败：{file}");
			}
		}
	}

	internal static void Adopt(LoadedModule run) {
		ModuleRegistry.Register(run);
		lock (Gate) {
			Loaded.Add(run);
		}
	}

	internal static LoadedModule LoadOne(string name, string file) {
		var assembly = LoadAssembly(file, name);
		var runtime = CreateRuntime(assembly, name);
		return new LoadedModule { Name = name, FilePath = file, Assembly = assembly, LoadedAt = DateTimeOffset.Now, Runtime = runtime };
	}

	public static void StartRuntime(LoadedModule run) {
		if (!InteractionHost.TryBeginModuleInitialization(run)) return;
		_ = ObserveInitAsync(run);
	}

	public static async Task<ModuleStopResult> StopAsync(LoadedModule run) {
		var task = run.GetOrStartStop(ct => run.Runtime.StopAsync(ct));
		try {
			return await task.WaitAsync(StopTimeout);
		} catch (TimeoutException) {
			Logger.Error(typeof(ModuleHost), $"模块停止等待超时（{StopTimeout.TotalSeconds}s），实际清理任务保留：{run.Name}");
			return new ModuleStopResult(run, ModuleStopOutcome.PendingTimeout, "等待超时，实际清理任务仍在运行");
		}
	}

	public static async Task<IReadOnlyList<ModuleStopResult>> StopAllAsync() {
		LoadedModule[] runs;
		lock (Gate) {
			runs = [.. Loaded];
		}
		var results = await Task.WhenAll(runs.Select(StopAsync));
		return results;
	}

	internal static void ResetForTest() {
		lock (Gate) {
			Loaded.Clear();
			_scanned = false;
		}
		ModuleRegistry.ForgetAll();
	}

	private static Assembly LoadAssembly(string file, string name) {
		Assembly assembly;
		try {
			assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(file));
		} catch (Exception e) {
			Logger.Error(typeof(ModuleHost), e, $"模块程序集读取失败：{name}");
			throw;
		}
		lock (Gate) {
			if (Assemblies.TryGetValue(assembly.FullName ?? name, out var existing) && !ReferenceEquals(existing, assembly)) {
				throw new InvalidOperationException($"模块程序集身份冲突：{name} 的程序集 {assembly.FullName} 已由另一个文件装载");
			}
			Assemblies[assembly.FullName ?? name] = assembly;
		}
		return assembly;
	}

	private static async Task ObserveInitAsync(LoadedModule run) {
		var initOk = false;
		try {
			await run.InitTask!;
			initOk = true;
		} catch (OperationCanceledException) when (run.Lifecycle.IsCancellationRequested) {
			Logger.Info(typeof(ModuleHost), $"模块初始化随生命周期取消：{run.Name}");
		} catch (Exception e) {
			Logger.Error(typeof(ModuleHost), e, $"模块初始化失败，已请求唯一停止清理：{run.Name}");
			run.MarkInitFailed("初始化失败");
		}
		if (initOk && InteractionHost.TryMarkModuleReady(run)) {
			Logger.Info(typeof(ModuleHost), $"模块初始化完成：{run.Name}");
			return;
		}
		var result = await run.GetOrStartStop(ct => run.Runtime.StopAsync(ct));
		if (!result.Clean) {
			Logger.Error(typeof(ModuleHost), $"模块 {run.Name} 清理未干净结束（{result.Outcome}）：{result.Reason}");
		}
	}

	private static IModuleRuntime CreateRuntime(Assembly assembly, string name) {
		Type[] types;
		try {
			types = assembly.GetTypes();
		} catch (ReflectionTypeLoadException e) {
			throw new InvalidOperationException($"模块类型扫描失败：{name}：{string.Join("; ", e.LoaderExceptions.Select(ex => ex?.Message))}");
		}
		var runtimeTypes = types.Where(type => !type.IsAbstract && !type.IsInterface && typeof(IModuleRuntime).IsAssignableFrom(type)).ToList();
		if (runtimeTypes.Count != 1) {
			throw new InvalidOperationException($"模块必须且只能有一个 IModuleRuntime 实现（发现 {runtimeTypes.Count} 个）：{name}");
		}
		return (IModuleRuntime)Activator.CreateInstance(runtimeTypes[0])!;
	}
}
