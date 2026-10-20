using System.Reflection;
using System.Runtime.Loader;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;
using Pengin1011.Core.Modules;
using Pengin1011.Services.AI;
using Pengin1011.Services.Discord;

namespace Pengin1011.Core;

public static class ModuleHost {
	internal static TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
	internal static TimeSpan CleanupBudget = TimeSpan.FromSeconds(10);

	private static readonly object _gate = new();
	private static readonly List<LoadedModule> _loaded = [];
	private static readonly Dictionary<string, Assembly> _assemblies = new(StringComparer.OrdinalIgnoreCase);
	private static bool _scanned;
	internal static string? ModuleDirectoryOverrideForTest;

	public static string ModuleDirectory => ModuleDirectoryOverrideForTest ?? Path.Combine(AppContext.BaseDirectory, "module");

	public static IReadOnlyList<LoadedModule> Modules {
		get {
			lock (_gate) {
				return [.. _loaded];
			}
		}
	}

	public static LoadedModule? Find(string name) {
		lock (_gate) {
			return _loaded.FirstOrDefault(module => string.Equals(module.Name, name, StringComparison.OrdinalIgnoreCase));
		}
	}

	public static void LoadAll() {
		lock (_gate) {
			if (_scanned) {
				Logger.Info(typeof(ModuleHost), Loc.Get("ModuleScanAlreadyDone"));
				return;
			}
			_scanned = true;
		}
		if (!Directory.Exists(ModuleDirectory)) {
			Logger.Info(typeof(ModuleHost), Loc.Format("ModuleDirectoryMissing", ModuleDirectory));
			return;
		}
		var files = Directory.EnumerateFiles(ModuleDirectory, "*.dll", SearchOption.TopDirectoryOnly)
			.OrderBy(file => file, StringComparer.Ordinal)
			.ToList();
		foreach (var file in files) {
			var name = Path.GetFileNameWithoutExtension(file);
			lock (_gate) {
				if (_loaded.Any(module => module.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
			}
			try {
				Adopt(LoadOne(name, file));
			} catch (Exception e) {
				Logger.Error(typeof(ModuleHost), e, Loc.Format("ModuleLoadFailed", file));
			}
		}
	}

	internal static void Adopt(LoadedModule run) {
		ModuleRegistry.Register(run);
		lock (_gate) {
			_loaded.Add(run);
		}
	}

	internal static LoadedModule LoadOne(string name, string file) {
		var assembly = LoadAssembly(file, name);
		var fileName = Path.GetFileNameWithoutExtension(file);
		var assemblyName = assembly.GetName().Name;
		if (!string.Equals(fileName, assemblyName, StringComparison.OrdinalIgnoreCase)) {
			throw new InvalidOperationException(Loc.Format("ModuleAssemblyNameMismatch", fileName, assemblyName));
		}
		if (assembly != typeof(ModuleHost).Assembly) {
			if (!ModuleHostContract.TryRead(assembly, out var contractVersion, out var contractFailure)) {
				throw new InvalidOperationException(Loc.Format("HostContractMissing", name, contractFailure ?? "missing"));
			}
			if (contractVersion != ModuleHostContract.Current) {
				throw new InvalidOperationException(Loc.Format("HostContractMismatch", name, contractVersion, ModuleHostContract.Current));
			}
		}
		using var registration = new ModuleRegistration(name);
		try {
			Localizer.RegisterModule(assembly);
		} catch (Exception e) {
			Logger.Error(typeof(ModuleHost), e, Loc.Format("ModuleLocalizationLoadFailed", name));
		}
		var runtime = CreateRuntime(assembly, name);
		var run = new LoadedModule { Name = name, FilePath = file, Assembly = assembly, LoadedAt = DateTimeOffset.Now, Runtime = runtime };
		var usages = AIPresetUsageRegistry.BeginRegistration(assembly, name);
		if (usages != null) {
			registration.Track(() => AIPresetUsageRegistry.Rollback(usages));
			AIPresetUsageRegistry.Commit(usages);
		}
		ModuleBridgeDispatcher.RegisterModule(assembly, run);
		registration.Track(() => ModuleBridgeDispatcher.RemoveModule(run.Name));
		registration.Commit();
		return run;
	}

	public static void StartRuntime(LoadedModule run) {
		if (!InteractionHost.TryBeginModuleInitialization(run)) return;
		_ = ObserveInitAsync(run);
	}

	public static async Task<ModuleStopResult> StopAsync(LoadedModule run) {
		ModuleBridgeDispatcher.CloseAdmission(run);
		var task = run.GetOrStartStop(ct => StopCoreAsync(run, ct));
		try {
			return await task.WaitAsync(StopTimeout);
		} catch (TimeoutException) {
			Logger.Error(typeof(ModuleHost), Loc.Format("ModuleStopWaitTimeout", StopTimeout.TotalSeconds, run.Name));
			return new ModuleStopResult(run, ModuleStopOutcome.PendingTimeout, Loc.Get("ModuleStopWaitTimeoutReason"));
		}
	}

	private static async Task StopCoreAsync(LoadedModule run, CancellationToken ct) {
		await DrainBridgeCallsAsync(run, ct);
		await run.Runtime.StopAsync(ct);
	}

	private static async Task DrainBridgeCallsAsync(LoadedModule run, CancellationToken ct) {
		while (true) {
			var active = ModuleBridgeDispatcher.ActiveCallsOf(run);
			if (active == 0) return;
			ct.ThrowIfCancellationRequested();
			await Task.Delay(20, ct);
		}
	}

	public static async Task<IReadOnlyList<ModuleStopResult>> StopAllAsync() {
		LoadedModule[] runs;
		lock (_gate) {
			runs = [.. _loaded];
		}
		var results = await Task.WhenAll(runs.Select(StopAsync));
		return results;
	}

	internal static void ResetForTest() {
		lock (_gate) {
			_loaded.Clear();
			_scanned = false;
		}
		ModuleRegistry.ForgetAll();
		ModuleBridgeDispatcher.ForgetAll();
		AIPresetUsageRegistry.ForgetAll();
	}

	private static Assembly LoadAssembly(string file, string name) {
		Assembly assembly;
		try {
			assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(file));
		} catch (Exception e) {
			Logger.Error(typeof(ModuleHost), e, Loc.Format("ModuleAssemblyReadFailed", name));
			throw;
		}
		lock (_gate) {
			if (_assemblies.TryGetValue(assembly.FullName ?? name, out var existing) && !ReferenceEquals(existing, assembly)) {
				throw new InvalidOperationException(Loc.Format("ModuleAssemblyIdentityConflict", name, assembly.FullName));
			}
			_assemblies[assembly.FullName ?? name] = assembly;
		}
		return assembly;
	}

	private static async Task ObserveInitAsync(LoadedModule run) {
		var initOk = false;
		try {
			await run.InitTask!;
			initOk = true;
		} catch (OperationCanceledException) when (run.Lifecycle.IsCancellationRequested) {
			Logger.Info(typeof(ModuleHost), Loc.Format("ModuleInitCancelled", run.Name));
		} catch (Exception e) {
			Logger.Error(typeof(ModuleHost), e, Loc.Format("ModuleInitFailed", run.Name));
			run.MarkInitFailed(Loc.Get("InitFailed"));
		}
		if (initOk && InteractionHost.TryMarkModuleReady(run)) {
			Logger.Info(typeof(ModuleHost), Loc.Format("ModuleInitCompleted", run.Name));
			return;
		}
		var result = await run.GetOrStartStop(ct => StopCoreAsync(run, ct));
		if (!result.Clean) {
			Logger.Error(typeof(ModuleHost), Loc.Format("ModuleCleanupUnclean", run.Name, result.Outcome, result.Reason));
		}
	}

	private static IModuleRuntime CreateRuntime(Assembly assembly, string name) {
		Type[] types;
		try {
			types = assembly.GetTypes();
		} catch (ReflectionTypeLoadException e) {
			throw new InvalidOperationException(Loc.Format("ModuleTypeScanFailed", name, string.Join("; ", e.LoaderExceptions.Select(ex => ex?.Message))));
		}
		var runtimeTypes = types.Where(type => !type.IsAbstract && !type.IsInterface && typeof(IModuleRuntime).IsAssignableFrom(type)).ToList();
		if (runtimeTypes.Count != 1) {
			throw new InvalidOperationException(Loc.Format("ModuleRuntimeCountInvalid", runtimeTypes.Count, name));
		}
		return (IModuleRuntime)Activator.CreateInstance(runtimeTypes[0])!;
	}
}
