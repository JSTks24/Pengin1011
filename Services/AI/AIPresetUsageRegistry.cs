using System.Reflection;
using Pengin1011.Core;
using Pengin1011.Core.Logging;

namespace Pengin1011.Services.AI;

public static class AIPresetUsageRegistry {
	private static readonly object _gate = new();
	private static readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
	private static int _sequence;

	private sealed record Registration(string Token, string Owner, IReadOnlyList<AIPresetUsageEntry> Entries);

	public static IReadOnlyList<AIPresetUsageEntry> Usages {
		get {
			lock (_gate) {
				var list = new List<AIPresetUsageEntry>();
				foreach (var registration in _registrations.Values) list.AddRange(registration.Entries);
				return list;
			}
		}
	}

	public static AIPresetUsageBatch? BeginRegistration(Assembly assembly, string? ownerName) {
		AIPresetUsageProviderAttribute? attribute;
		try {
			attribute = assembly.GetCustomAttribute<AIPresetUsageProviderAttribute>();
		} catch (Exception e) {
			Logger.Error(typeof(AIPresetUsageRegistry), e, Loc.Format("AIPresetUsageProviderInvalid", ownerName ?? assembly.GetName().Name ?? "?"));
			throw;
		}
		if (attribute == null) return null;
		var owner = ownerName ?? assembly.GetName().Name ?? "?";
		var token = $"{owner}#{Interlocked.Increment(ref _sequence):x}";
		var batch = new AIPresetUsageBatch(token, owner);
		try {
			var provider = (IAIPresetUsageProvider)Activator.CreateInstance(attribute.ProviderType)!;
			provider.Register(new AIPresetUsageRegistrar(batch));
		} catch (Exception e) {
			Logger.Error(typeof(AIPresetUsageRegistry), e, Loc.Format("AIPresetUsageProviderInvalid", owner));
			throw;
		}
		return batch;
	}

	public static void Register(Assembly assembly, string? ownerName = null) {
		var batch = BeginRegistration(assembly, ownerName);
		if (batch != null) Commit(batch);
	}

	internal static void Commit(AIPresetUsageBatch batch) {
		lock (_gate) {
			RemoveOwnerCore(batch.Owner);
			_registrations[batch.Token] = new Registration(batch.Token, batch.Owner, [.. batch.Entries]);
		}
	}

	internal static void Rollback(AIPresetUsageBatch batch) {
		lock (_gate) {
			_registrations.Remove(batch.Token);
		}
	}

	public static IReadOnlyList<string> ReferencesTo(string presetName) {
		var references = new List<string>();
		lock (_gate) {
			foreach (var registration in _registrations.Values) {
				foreach (var entry in registration.Entries) {
					if (entry.Dependency.IsDefault) continue;
					if (!string.Equals(entry.Dependency.Name, presetName, StringComparison.Ordinal)) continue;
					references.Add($"{entry.Owner}: {entry.CallSite}");
				}
			}
		}
		references.Sort(StringComparer.Ordinal);
		return references;
	}

	public static bool HasDefaultConsumers {
		get {
			lock (_gate) {
				foreach (var registration in _registrations.Values) {
					foreach (var entry in registration.Entries) {
						if (entry.Dependency.IsDefault) return true;
					}
				}
			}
			return false;
		}
	}

	internal static void RemoveOwner(string owner) {
		lock (_gate) {
			RemoveOwnerCore(owner);
		}
	}

	private static void RemoveOwnerCore(string owner) {
		var stale = new List<string>();
		foreach (var registration in _registrations.Values) {
			if (string.Equals(registration.Owner, owner, StringComparison.Ordinal)) stale.Add(registration.Token);
		}
		foreach (var token in stale) _registrations.Remove(token);
	}

	internal static void ForgetAll() {
		lock (_gate) {
			_registrations.Clear();
		}
	}

	internal static void LoadForTest(IReadOnlyList<AIPresetUsageEntry> entries) {
		lock (_gate) {
			_registrations.Clear();
			_registrations["test"] = new Registration("test", "test", [.. entries]);
		}
	}

	internal static AIPresetUsageBatch CreateBatchForTest(string owner, params (string CallSite, string PresetName)[] entries) {
		var batch = new AIPresetUsageBatch($"{owner}#test{Interlocked.Increment(ref _sequence):x}", owner);
		foreach (var (callSite, presetName) in entries) {
			batch.Add(callSite, string.IsNullOrEmpty(presetName) ? AIPresetDependency.Default : AIPresetDependency.Named(presetName));
		}
		return batch;
	}
}
