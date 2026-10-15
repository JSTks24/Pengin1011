using System.Collections.Concurrent;
using System.Reflection;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;
using Pengin1011.Core.Modules;

namespace Pengin1011.Core;

public static class ModuleBridgeDispatcher {
	internal sealed class Export {
		public required LoadedModule Owner { get; init; }
		public required Delegate Handler { get; init; }
		public int ActiveCalls;
		public bool AdmissionOpen = true;
	}

	private sealed class ExportKeyComparer : IEqualityComparer<(string Module, string Name)> {
		public bool Equals((string Module, string Name) x, (string Module, string Name) y) {
			return string.Equals(x.Module, y.Module, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Name, y.Name, StringComparison.Ordinal);
		}

		public int GetHashCode((string Module, string Name) obj) {
			return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Module) ^ StringComparer.Ordinal.GetHashCode(obj.Name);
		}
	}

	private static readonly object _gate = new();
	private static readonly ConcurrentDictionary<(string Module, string Name), Export> _exports = new(new ExportKeyComparer());

	public sealed class BridgeLease : IDisposable {
		private Export? _export;

		internal BridgeLease(Export export) {
			_export = export;
		}

		public void Dispose() {
			var export = Interlocked.Exchange(ref _export, null);
			if (export == null) return;
			ReleaseCall(export);
		}
	}

	public sealed class BridgeCall<TDelegate> where TDelegate : Delegate {
		internal BridgeCall(TDelegate handler, BridgeLease lease) {
			Handler = handler;
			Lease = lease;
		}

		public TDelegate Handler { get; }

		public BridgeLease Lease { get; }
	}

	public static void RegisterModule(Assembly assembly, LoadedModule run) {
		ModuleBridgeProviderAttribute? providerAttribute;
		try {
			providerAttribute = assembly.GetCustomAttribute<ModuleBridgeProviderAttribute>();
		} catch (Exception e) {
			Logger.Error(typeof(ModuleBridgeDispatcher), e, Loc.Format("ModuleBridgeProviderInvalid", run.Name));
			throw;
		}
		if (providerAttribute == null) return;
		RemoveExportsOf(run);
		var batch = new List<(string Module, string Name)>();
		try {
			var provider = (IModuleBridgeProvider)Activator.CreateInstance(providerAttribute.ProviderType)!;
			provider.Register(new ModuleBridgeRegistrar(run, batch), run.Runtime);
		} catch (Exception e) {
			foreach (var key in batch) _exports.TryRemove(key, out _);
			Logger.Error(typeof(ModuleBridgeDispatcher), e, Loc.Format("ModuleBridgeProviderInvalid", run.Name));
			throw;
		}
	}

	private static void RemoveExportsOf(LoadedModule run) {
		foreach (var pair in _exports) {
			var owner = pair.Value.Owner;
			var sameRun = ReferenceEquals(owner, run);
			var sameModule = owner.Assembly == run.Assembly && string.Equals(owner.Name, run.Name, StringComparison.OrdinalIgnoreCase);
			if (sameRun || sameModule) _exports.TryRemove(pair.Key, out _);
		}
	}

	public static BridgeCall<TDelegate> Admit<TDelegate>(string module, string name) where TDelegate : Delegate {
		lock (_gate) {
			if (!_exports.TryGetValue((module, name), out var export)) {
				throw new KeyNotFoundException(Loc.Format("ModuleBridgeExportNotFound", module, name));
			}
			if (!export.AdmissionOpen || export.Owner.State != ModuleState.Ready) {
				throw new InvalidOperationException(Loc.Format("ModuleBridgeTargetNotReady", module, name, export.Owner.State));
			}
			if (export.Handler is not TDelegate handler) {
				throw new InvalidOperationException(Loc.Format("ModuleBridgeSignatureMismatch", module, name, typeof(TDelegate), export.Handler.GetType()));
			}
			export.ActiveCalls++;
			return new BridgeCall<TDelegate>(handler, new BridgeLease(export));
		}
	}

	internal static int ActiveCallsOf(LoadedModule run) {
		lock (_gate) {
			var total = 0;
			foreach (var export in _exports.Values) {
				if (ReferenceEquals(export.Owner, run)) total += export.ActiveCalls;
			}
			return total;
		}
	}

	internal static void CloseAdmission(LoadedModule run) {
		lock (_gate) {
			foreach (var export in _exports.Values) {
				if (ReferenceEquals(export.Owner, run)) export.AdmissionOpen = false;
			}
		}
	}

	internal static void OpenAdmission(LoadedModule run) {
		lock (_gate) {
			foreach (var export in _exports.Values) {
				if (ReferenceEquals(export.Owner, run)) export.AdmissionOpen = true;
			}
		}
	}

	internal static void AddExport(LoadedModule owner, string name, Delegate handler, List<(string Module, string Name)> batch) {
		if (!_exports.TryAdd((owner.Name, name), new Export { Owner = owner, Handler = handler })) {
			Logger.Error(typeof(ModuleBridgeDispatcher), Loc.Format("ModuleBridgeDuplicateExport", owner.Name, name));
			throw new InvalidOperationException(Loc.Format("ModuleBridgeDuplicateExport", owner.Name, name));
		}
		batch.Add((owner.Name, name));
	}

	internal static void RemoveModule(string name) {
		foreach (var key in _exports.Keys) {
			if (string.Equals(key.Module, name, StringComparison.OrdinalIgnoreCase)) _exports.TryRemove(key, out _);
		}
	}

	private static void ReleaseCall(Export export) {
		lock (_gate) {
			export.ActiveCalls--;
		}
	}

	internal static void ForgetAll() {
		_exports.Clear();
	}
}
