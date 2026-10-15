using System.Reflection;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;
using Pengin1011.Core.Modules;

namespace Pengin1011.Core;

public enum ModuleStopOutcome {
	Clean,
	PendingTimeout,
	Failed,
}

public sealed record ModuleStopResult(LoadedModule Module, ModuleStopOutcome Outcome, string? Reason) {
	public bool Clean => Outcome == ModuleStopOutcome.Clean;
}

public sealed class LoadedModule {
	private readonly object _syncGate = new();
	private Task? _initTask;
	private Task<ModuleStopResult>? _stopTask;

	public required string Name { get; init; }
	public required string FilePath { get; init; }
	public required Assembly Assembly { get; init; }
	public required DateTimeOffset LoadedAt { get; init; }
	public required IModuleRuntime Runtime { get; init; }

	public ModuleState State { get; internal set; } = ModuleState.Starting;
	public string? DisabledReason { get; internal set; }
	public CancellationTokenSource Lifecycle { get; } = new();

	public Task? InitTask {
		get {
			lock (_syncGate) {
				return _initTask;
			}
		}
	}

	public ModuleStopResult? FinalStopResult {
		get {
			lock (_syncGate) {
				return _stopTask is { IsCompletedSuccessfully: true } task ? task.Result : null;
			}
		}
	}

	internal Task? TryBeginInit(Func<Task> body) {
		lock (_syncGate) {
			if (_initTask != null || _stopTask != null || State != ModuleState.Starting) return null;
			_initTask = Task.Run(body);
			return _initTask;
		}
	}

	internal bool TryMarkReady() {
		lock (_syncGate) {
			if (State != ModuleState.Starting || _stopTask != null) return false;
			State = ModuleState.Ready;
			return true;
		}
	}

	internal void MarkInitFailed(string reason) {
		lock (_syncGate) {
			if (State != ModuleState.Starting) return;
			State = ModuleState.Disabled;
			DisabledReason = reason;
		}
	}

	internal Task<ModuleStopResult> GetOrStartStop(Func<CancellationToken, Task> cleanup) {
		lock (_syncGate) {
			if (_stopTask != null) return _stopTask;
			if (State is ModuleState.Starting or ModuleState.Ready) {
				State = ModuleState.Stopping;
				DisabledReason = null;
			}
			var lifecycle = Lifecycle;
			var name = Name;
			_stopTask = Task.Run(async () => {
				try {
					lifecycle.Cancel();
				} catch (Exception e) {
					Logger.Error(typeof(LoadedModule), e, Loc.Format("ModuleLifecycleCancelFailed", name));
				}
				var init = InitTask;
				if (init != null) {
					try {
						await init;
					} catch (Exception e) {
						Logger.Info(typeof(LoadedModule), Loc.Format("ModuleInitEndedFaulted", name, e.Message));
					}
				}
				using var budget = new CancellationTokenSource(ModuleHost.CleanupBudget);
				try {
					await cleanup(budget.Token);
					lock (_syncGate) {
						if (State == ModuleState.Stopping) State = ModuleState.Stopped;
					}
					return new ModuleStopResult(this, ModuleStopOutcome.Clean, null);
				} catch (OperationCanceledException) when (budget.IsCancellationRequested) {
					lock (_syncGate) {
						State = ModuleState.Disabled;
						DisabledReason = Loc.Get("CleanupBudgetExceeded");
					}
					return new ModuleStopResult(this, ModuleStopOutcome.Failed, Loc.Get("CleanupBudgetExceededDetail"));
				} catch (Exception e) {
					Logger.Error(typeof(LoadedModule), e, Loc.Format("ModuleCleanupFailed", name));
					lock (_syncGate) {
						State = ModuleState.Disabled;
						DisabledReason = e.Message;
					}
					return new ModuleStopResult(this, ModuleStopOutcome.Failed, e.Message);
				}
			});
			return _stopTask;
		}
	}

	public override string ToString() {
		return Loc.Format("ModuleToString", Name, State);
	}
}
