using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Pengin1011.Core;
using Pengin1011.Core.Logging;

namespace Pengin1011.Services.AI;

public enum AIRole {
	User,
	Assistant,
}

public sealed record AIMessage(AIRole Role, string Text);
public sealed record AIImage(byte[] Data, string MimeType);
public sealed record AIUsage(int InputTokens, int OutputTokens);
public sealed record AIResult(string Text, AIUsage Usage);
public sealed record AIStreamDelta(string Text, AIUsage? Usage = null);

public sealed class AIRequest {
	public string System { get; init; } = "";
	public required IReadOnlyList<AIMessage> Messages { get; init; }
	public IReadOnlyList<AIImage> Images { get; init; } = [];
	public string? Model { get; init; }
	public float? Temperature { get; init; }
	public float? TopP { get; init; }
	public int? MaxOutputTokens { get; init; }
}

public interface IAIBackend {
	Task<AIResult?> CompleteAsync(AIRequest request, CancellationToken ct);
	IAsyncEnumerable<AIStreamDelta> StreamAsync(AIRequest request, CancellationToken ct);
}

public sealed class AIPresetNotFoundException(string message) : KeyNotFoundException(message);

public sealed record AIClientShutdownResult(bool Drained, int ActiveLeases);

internal enum BackendState {
	Creating,
	Ready,
	Retired,
	Disposed,
}

public static class AIClient {
	public const int CallTimeoutMs = 120_000;
	public const int RetryCount = 2;
	public static readonly TimeSpan DefaultShutdownBudget = TimeSpan.FromSeconds(10);

	private static readonly int[] _retryDelaysMs = [1_000, 2_000];
	private static readonly object _cacheLock = new();
	private static readonly Dictionary<AIConnectionKey, BackendEntry> _entries = [];
	private static readonly HashSet<BackendEntry> _liveEntries = [];
	private static SemaphoreSlim _gate = new(1, 1);
	private static Func<AIPreset, IAIBackend> _backendFactory = DefaultCreateBackend;
	private static int _activeAdmissions;
	private static bool _accepting = true;

	private sealed class BackendEntry {
		public required AIConnectionKey Key { get; init; }
		public TaskCompletionSource Creation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public BackendState State { get; set; } = BackendState.Creating;
		public IAIBackend? Backend { get; set; }
		public ExceptionDispatchInfo? CreationError { get; set; }
		public int ActiveLeases { get; set; }
	}

	private sealed class BackendLease(BackendEntry entry) : IDisposable {
		private BackendEntry? _entry = entry;

		public IAIBackend Backend => _entry?.Backend ?? throw new ObjectDisposedException(nameof(BackendLease));

		public void Dispose() {
			var entry = Interlocked.Exchange(ref _entry, null);
			if (entry != null) ReleaseLease(entry);
		}
	}

	public static void Init(int maxParallel, Func<AIPreset, IAIBackend>? backendFactory = null) {
		InvalidateBackendCache();
		lock (_cacheLock) {
			if (_activeAdmissions > 0 || _liveEntries.Count > 0) throw new InvalidOperationException(Loc.Get("AIClientShuttingDown"));
			_backendFactory = backendFactory ?? DefaultCreateBackend;
			var parallelism = Math.Max(1, maxParallel);
			_gate = new SemaphoreSlim(parallelism, parallelism);
			_accepting = true;
		}
	}

	public static void Shutdown() {
		ShutdownAsync(DefaultShutdownBudget).GetAwaiter().GetResult();
	}

	public static async Task<AIClientShutdownResult> ShutdownAsync(TimeSpan budget, CancellationToken ct = default) {
		lock (_cacheLock) _accepting = false;
		foreach (var entry in RetireConnections(false)) _ = Task.Run(() => DisposeEntry(entry));
		var deadline = DateTime.UtcNow + budget;
		while (true) {
			int outstanding;
			lock (_cacheLock) {
				outstanding = _activeAdmissions + _liveEntries.Count(entry => entry.ActiveLeases == 0);
			}
			if (outstanding == 0) return new AIClientShutdownResult(true, 0);
			if (DateTime.UtcNow >= deadline) {
				Logger.Error(typeof(AIClient), Loc.Format("AIShutdownNotDrained", outstanding));
				return new AIClientShutdownResult(false, outstanding);
			}
			await Task.Delay(20, ct);
		}
	}

	public static void InvalidateBackendCache() {
		foreach (var entry in RetireConnections(false)) DisposeEntry(entry);
	}

	internal static void RefreshConnections() {
		foreach (var entry in RetireConnections(true)) DisposeEntry(entry);
	}

	public static Task<AIResult?> CompleteAsync(AIRequest request, CancellationToken ct = default) {
		return CompleteCoreAsync(null, request, ct);
	}

	public static Task<AIResult?> CompleteAsync(string? presetName, AIRequest request, CancellationToken ct = default) {
		return CompleteCoreAsync(presetName, request, ct);
	}

	public static IAsyncEnumerable<AIStreamDelta> StreamAsync(AIRequest request, CancellationToken ct = default) {
		return StreamEntryAsync(null, request, ct);
	}

	public static IAsyncEnumerable<AIStreamDelta> StreamAsync(string? presetName, AIRequest request, CancellationToken ct = default) {
		return StreamEntryAsync(presetName, request, ct);
	}

	private static IAsyncEnumerable<AIStreamDelta> StreamEntryAsync(string? presetName, AIRequest request, CancellationToken ct) {
		Validate(Merge(ResolvePreset(presetName), request));
		return StreamCoreAsync(presetName, request, ct);
	}

	private static async Task<AIResult?> CompleteCoreAsync(string? presetName, AIRequest request, CancellationToken ct) {
		Validate(Merge(ResolvePreset(presetName), request));
		var gate = EnterAdmission();
		var entered = false;
		BackendLease? lease = null;
		try {
			await gate.WaitAsync(ct);
			entered = true;
			var preset = ResolvePreset(presetName);
			var merged = Merge(preset, request);
			Validate(merged);
			for (var attempt = 0; ; attempt++) {
				try {
					lease ??= await AcquireLeaseAsync(preset, ct);
					return await AttemptAsync(lease.Backend, merged, ct);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					var transient = OpenAIBackend.IsTransient(e) || GeminiBackend.IsTransient(e);
					Logger.Error(typeof(AIClient), e, transient ? Loc.Format("AICallTransientFailure", attempt + 1) : Loc.Get("AICallFailed"));
					if (!transient || attempt >= RetryCount) return null;
					await Task.Delay(_retryDelaysMs[attempt], ct);
				}
			}
		} finally {
			lease?.Dispose();
			if (entered) gate.Release();
			ExitAdmission();
		}
	}

	private static async IAsyncEnumerable<AIStreamDelta> StreamCoreAsync(string? presetName, AIRequest request, [EnumeratorCancellation] CancellationToken ct) {
		var gate = EnterAdmission();
		var entered = false;
		BackendLease? lease = null;
		try {
			await gate.WaitAsync(ct);
			entered = true;
			var preset = ResolvePreset(presetName);
			var merged = Merge(preset, request);
			Validate(merged);
			lease = await AcquireLeaseAsync(preset, ct);
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(CallTimeoutMs);
			await foreach (var delta in lease.Backend.StreamAsync(merged, timeout.Token).WithCancellation(ct)) {
				yield return delta;
			}
		} finally {
			lease?.Dispose();
			if (entered) gate.Release();
			ExitAdmission();
		}
	}

	private static AIPreset ResolvePreset(string? presetName) {
		var snapshot = AIPresetStore.Snapshot;
		var preset = snapshot.Find(presetName);
		if (preset != null) return preset;
		var available = string.Join(Loc.Get("ListSeparatorItems"), snapshot.Presets.Select(preset => preset.Name));
		throw new AIPresetNotFoundException(string.IsNullOrEmpty(presetName)
			? Loc.Format("AIPresetDefaultMissing", available)
			: Loc.Format("AIPresetNameMissing", presetName, available));
	}

	private static SemaphoreSlim EnterAdmission() {
		lock (_cacheLock) {
			if (!_accepting) throw new InvalidOperationException(Loc.Get("AIClientShuttingDown"));
			_activeAdmissions++;
			return _gate;
		}
	}

	private static void ExitAdmission() {
		lock (_cacheLock) _activeAdmissions--;
	}

	private static async Task<BackendLease> AcquireLeaseAsync(AIPreset preset, CancellationToken ct) {
		var key = preset.Connection;
		BackendEntry entry;
		Func<AIPreset, IAIBackend>? factory = null;
		lock (_cacheLock) {
			if (!_entries.TryGetValue(key, out entry!)) {
				entry = new BackendEntry { Key = key };
				if (_accepting && AIPresetStore.Snapshot.Presets.Any(current => current.Connection.Equals(key))) {
					_entries[key] = entry;
				} else {
					entry.State = BackendState.Retired;
				}
				_liveEntries.Add(entry);
				factory = _backendFactory;
			}
			entry.ActiveLeases++;
		}
		if (factory != null) _ = CreateBackendAsync(entry, preset, factory);
		try {
			await entry.Creation.Task.WaitAsync(ct);
			entry.CreationError?.Throw();
			return new BackendLease(entry);
		} catch (Exception) {
			ReleaseLease(entry);
			throw;
		}
	}

	private static async Task CreateBackendAsync(BackendEntry entry, AIPreset preset, Func<AIPreset, IAIBackend> factory) {
		IAIBackend backend;
		try {
			backend = await Task.Run(() => factory(preset)).ConfigureAwait(false);
		} catch (Exception e) {
			lock (_cacheLock) {
				entry.CreationError = ExceptionDispatchInfo.Capture(e);
				entry.State = BackendState.Disposed;
				RemoveCurrentEntry(entry);
				_liveEntries.Remove(entry);
			}
			Logger.Error(typeof(AIClient), e, Loc.Format("AIBackendCreateFailed", entry.Key.Describe()));
			entry.Creation.TrySetResult();
			return;
		}
		var dispose = false;
		lock (_cacheLock) {
			entry.Backend = backend;
			if (entry.State == BackendState.Creating) entry.State = BackendState.Ready;
			dispose = entry.State == BackendState.Retired && entry.ActiveLeases == 0;
			if (dispose) entry.State = BackendState.Disposed;
		}
		entry.Creation.TrySetResult();
		if (dispose) DisposeEntry(entry);
	}

	private static void ReleaseLease(BackendEntry entry) {
		var dispose = false;
		lock (_cacheLock) {
			entry.ActiveLeases--;
			if (entry.ActiveLeases == 0 && entry.State == BackendState.Retired && entry.Backend != null) {
				entry.State = BackendState.Disposed;
				dispose = true;
			}
		}
		if (dispose) DisposeEntry(entry);
	}

	private static IReadOnlyList<BackendEntry> RetireConnections(bool keepCurrent) {
		var disposable = new List<BackendEntry>();
		lock (_cacheLock) {
			HashSet<AIConnectionKey> active = keepCurrent ? AIPresetStore.Snapshot.Presets.Select(preset => preset.Connection).ToHashSet() : [];
			foreach (var entry in _entries.Values.ToArray()) {
				if (active.Contains(entry.Key)) continue;
				RemoveCurrentEntry(entry);
				entry.State = BackendState.Retired;
				if (entry.ActiveLeases > 0 || entry.Backend == null) continue;
				entry.State = BackendState.Disposed;
				disposable.Add(entry);
			}
		}
		return disposable;
	}

	private static void RemoveCurrentEntry(BackendEntry entry) {
		if (_entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(entry, current)) _entries.Remove(entry.Key);
	}

	private static void DisposeEntry(BackendEntry entry) {
		try {
			if (entry.Backend is IDisposable disposable) disposable.Dispose();
		} catch (Exception e) {
			Logger.Error(typeof(AIClient), e, Loc.Format("AIBackendDisposeFailed", entry.Key.Describe()));
		} finally {
			lock (_cacheLock) {
				entry.Backend = null;
				_liveEntries.Remove(entry);
			}
		}
	}

	private static AIRequest Merge(AIPreset preset, AIRequest request) {
		if (request.Model != null && request.Temperature != null && request.TopP != null && request.MaxOutputTokens != null) return request;
		return new AIRequest {
			System = request.System,
			Messages = request.Messages,
			Images = request.Images,
			Model = request.Model ?? (preset.Model.Length > 0 ? preset.Model : null),
			Temperature = request.Temperature ?? preset.Temperature,
			TopP = request.TopP ?? preset.TopP,
			MaxOutputTokens = request.MaxOutputTokens ?? preset.MaxOutputTokens,
		};
	}

	private static void Validate(AIRequest request) {
		if (request.MaxOutputTokens is <= 0) throw new ArgumentException(Loc.Format("AIMaxOutputTokensInvalid", request.MaxOutputTokens));
		if (request.Temperature is float temperature && (float.IsNaN(temperature) || float.IsInfinity(temperature) || temperature < 0)) {
			throw new ArgumentException(Loc.Format("AITemperatureInvalid", request.Temperature));
		}
		if (request.TopP is float topP && (float.IsNaN(topP) || float.IsInfinity(topP) || topP < 0)) {
			throw new ArgumentException(Loc.Format("AITopPInvalid", request.TopP));
		}
		if (string.IsNullOrEmpty(request.Model)) throw new ArgumentException(Loc.Get("AIModelNotConfigured"));
	}

	private static IAIBackend DefaultCreateBackend(AIPreset preset) {
		return preset.Provider == AIPresetProvider.OpenAI ? new OpenAIBackend(preset) : new GeminiBackend(preset);
	}

	private static async Task<AIResult?> AttemptAsync(IAIBackend backend, AIRequest request, CancellationToken ct) {
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(CallTimeoutMs);
		return await backend.CompleteAsync(request, timeout.Token);
	}
}
