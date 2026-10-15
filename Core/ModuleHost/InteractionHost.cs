using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;
using Pengin1011.Core.Modules;
using Pengin1011.Services.Discord;

namespace Pengin1011.Core;

public static class InteractionHost {
	internal static TimeSpan SyncStopWaitForTest = TimeSpan.FromSeconds(5);

	private static readonly SemaphoreSlim _controlGate = new(1, 1);
	private static readonly object _exitSync = new();
	private static readonly object _syncGate = new();
	private static InteractionSnapshot? _current;
	private static CancellationTokenSource _shutdownCts = new();
	private static bool _exiting;
	private static bool _syncExecuting;
	private static bool _syncPending;
	private static Task? _syncTask;
	private static int _retiredServices;
	private static int _createdServices;

	internal static Func<CancellationToken, Task>? SyncBodyForTest;
	internal static Func<Task>? PublishBarrierForTest;

	public static CancellationToken ShutdownToken => _shutdownCts.Token;

	public static bool Exiting {
		get {
			lock (_exitSync) {
				return _exiting;
			}
		}
	}

	public static InteractionSnapshot Current => _current ?? throw new InvalidOperationException(Loc.Get("InteractionServiceNotInitialized"));

	public static void RequestShutdown() {
		lock (_exitSync) {
			_exiting = true;
		}
		DiscordGateway.RequestExitClose();
		try {
			_shutdownCts.Cancel();
		} catch (ObjectDisposedException) {
		}
	}

	internal static bool TryBeginModuleInitialization(LoadedModule run) {
		lock (_exitSync) {
			if (_exiting) return false;
			return run.TryBeginInit(() => run.Runtime.InitializeAsync(run.Lifecycle.Token)) != null;
		}
	}

	internal static bool TryMarkModuleReady(LoadedModule run) {
		lock (_exitSync) {
			if (_exiting) return false;
			return run.TryMarkReady();
		}
	}

	public static async Task PublishInitialAsync() {
		await _controlGate.WaitAsync(ShutdownToken);
		try {
			lock (_exitSync) {
				if (_exiting || _current != null) {
					return;
				}
			}
			var runs = ModuleHost.Modules;
			var service = CreateService();
			var attach = await AttachAsync(service, runs);
			var routable = attach.Valid;
			foreach (var error in attach.Errors) {
				Logger.Error(typeof(InteractionHost), Loc.Format("StartupAttachContractError", error));
			}
			if (attach.Invalid.Count > 0) {
				RetireService(service);
				service = CreateService();
				var rebuilt = await AttachAsync(service, attach.Valid);
				foreach (var error in rebuilt.Errors) {
					Logger.Error(typeof(InteractionHost), Loc.Format("StartupRebuildContractError", error));
				}
				routable = rebuilt.Valid;
				foreach (var run in attach.Invalid.Concat(rebuilt.Invalid)) {
					run.MarkInitFailed(Loc.Get("StartupContractCheckFailed"));
				}
			}
			var published = false;
			lock (_exitSync) {
				if (_exiting) {
					RetireService(service);
				} else {
					_current = new InteractionSnapshot(service, routable);
					published = true;
				}
			}
			if (!published) return;
			var barrier = PublishBarrierForTest;
			if (barrier != null) {
				await barrier();
			}
			foreach (var run in routable) {
				ModuleHost.StartRuntime(run);
			}
			DiscordGateway.OpenAccepting();
		} finally {
			_controlGate.Release();
		}
	}

	public static async Task ExecuteAsync(SocketInteraction interaction, CancellationToken ct) {
		if (interaction is SocketMessageComponent or SocketModal) return;
		var snapshot = Current;
		var context = new SocketInteractionContext(DiscordGateway.Client!, interaction);
		await snapshot.Service.ExecuteCommandAsync(context, EmptyServiceProvider.Instance);
	}

	internal static void OnGatewayReady() {
		lock (_syncGate) {
			if (_syncExecuting) {
				_syncPending = true;
				return;
			}
			_syncExecuting = true;
			_syncTask = Task.Run(RunReadySyncAsync);
		}
	}

	internal static Task? SyncTaskForTest {
		get {
			lock (_syncGate) {
				return _syncTask;
			}
		}
	}

	internal static bool SyncPendingForTest {
		get {
			lock (_syncGate) {
				return _syncPending;
			}
		}
	}

	internal static int ControlGateCountForTest => _controlGate.CurrentCount;

	internal static InteractionSnapshot? SnapshotForTest => _current;

	internal static int RetiredServiceCountForTest => Volatile.Read(ref _retiredServices);

	internal static int CreatedServiceCountForTest => Volatile.Read(ref _createdServices);

	private static async Task RunReadySyncAsync() {
		while (true) {
			try {
				using var linked = CancellationTokenSource.CreateLinkedTokenSource(CurrentShutdownToken());
				await _controlGate.WaitAsync(linked.Token);
				try {
					if (!Exiting) {
						await SyncBodyAsync(linked.Token);
					}
				} finally {
					_controlGate.Release();
				}
			} catch (OperationCanceledException) {
			} catch (Exception e) {
				Logger.Error(typeof(InteractionHost), e, Loc.Get("ReadySyncFailed"));
			}
			lock (_syncGate) {
				if (!_syncPending || Exiting) {
					_syncExecuting = false;
					_syncPending = false;
					return;
				}
				_syncPending = false;
			}
		}
	}

	private static async Task SyncBodyAsync(CancellationToken ct) {
		var hook = SyncBodyForTest;
		if (hook != null) {
			await hook(ct);
			return;
		}
		await RegisterCommandsSafeAsync();
	}

	private static CancellationToken CurrentShutdownToken() {
		try {
			return _shutdownCts.Token;
		} catch (ObjectDisposedException) {
			return new CancellationToken(true);
		}
	}

	public static async Task<string?> RegisterCommandsSafeAsync() {
		try {
			await Current.Service.RegisterCommandsGloballyAsync();
			Logger.Info(typeof(InteractionHost), Loc.Get("CommandGlobalRegisterCompleted"));
			return null;
		} catch (Exception e) {
			Logger.Error(typeof(InteractionHost), e, Loc.Get("CommandGlobalRegisterFailed"));
			return e.Message;
		}
	}

	public static async Task<string?> SyncAsync(CancellationToken ct) {
		if (Exiting) return Loc.Get("FrameworkExiting");
		await _controlGate.WaitAsync(ct);
		try {
			if (Exiting) return Loc.Get("FrameworkExiting");
			return await RegisterCommandsSafeAsync();
		} finally {
			_controlGate.Release();
		}
	}

	internal static async Task<bool> EnterControlSectionAsync(TimeSpan budget) {
		return await _controlGate.WaitAsync(budget);
	}

	internal static void LeaveControlSection() {
		_controlGate.Release();
	}

	internal static void ResetForTest() {
		InteractionSnapshot? snapshot;
		lock (_exitSync) {
			snapshot = _current;
			_current = null;
			_exiting = false;
		}
		if (snapshot != null) {
			RetireService(snapshot.Service);
		}
		_shutdownCts.Dispose();
		_shutdownCts = new CancellationTokenSource();
	}

	public static async Task<ServiceReleaseResult> ShutdownAsync() {
		Task? syncTask;
		lock (_syncGate) {
			syncTask = _syncTask;
		}
		if (syncTask != null && !syncTask.IsCompleted) {
			try {
				await syncTask.WaitAsync(SyncStopWaitForTest);
			} catch (TimeoutException) {
				var reason = Loc.Format("ReadySyncStopTimeout", SyncStopWaitForTest.TotalSeconds);
				Logger.Error(typeof(InteractionHost), reason);
				return new ServiceReleaseResult(false, reason);
			} catch (OperationCanceledException) {
			}
		}
		InteractionSnapshot? snapshot;
		lock (_exitSync) {
			snapshot = _current;
			_current = null;
		}
		if (snapshot != null) {
			RetireService(snapshot.Service);
		}
		return new ServiceReleaseResult(true, null);
	}

	internal static async Task<ModuleAttachResult> AttachAsync(InteractionService service, IReadOnlyList<LoadedModule> runs) {
		var valid = new List<LoadedModule>();
		var invalid = new List<LoadedModule>();
		var errors = new List<string>();
		foreach (var run in runs) {
			var runErrors = new List<string>();
			try {
				var built = await service.AddModulesAsync(run.Assembly, EmptyServiceProvider.Instance);
				foreach (var module in built) {
					CheckCommandContracts(module, runErrors, run);
				}
				if (runErrors.Count == 0) {
					valid.Add(run);
					Logger.Info(typeof(InteractionHost), Loc.Format("ModuleCommandsAttached", run.Name, built.Count()));
				} else {
					invalid.Add(run);
					errors.AddRange(runErrors);
					Logger.Error(typeof(InteractionHost), Loc.Format("ModuleAttachContractFailed", run.Name, runErrors.Count));
				}
			} catch (Exception e) {
				Logger.Error(typeof(InteractionHost), e, Loc.Format("ModuleAttachFailed", run.Name));
				invalid.Add(run);
				errors.Add($"{run.Name}: {e.Message}");
			}
		}
		return new ModuleAttachResult(valid, invalid, errors);
	}

	private static void CheckCommandContracts(ModuleInfo module, List<string> errors, LoadedModule run) {
		foreach (var command in module.SlashCommands.Cast<ICommandInfo>()
			.Concat(module.ContextCommands.Cast<ICommandInfo>())
			.Concat(module.ComponentCommands.Cast<ICommandInfo>())) {
			if (command.RunMode != RunMode.Sync) {
				errors.Add(Loc.Format("ContractRunModeOverride", run.Name, command.Name, command.RunMode));
			}
		}
		var attributes = module.Preconditions.OfType<ModuleAvailabilityAttribute>().ToList();
		if (!module.IsSubModule && attributes.Count == 0) {
			errors.Add(Loc.Format("ContractMissingAvailability", run.Name, module.Name));
		}
		foreach (var attribute in attributes) {
			if (attribute.RuntimeType != run.Runtime.GetType()) {
				errors.Add(Loc.Format("ContractAvailabilityMismatch", run.Name, module.Name, attribute.RuntimeType.Name, run.Runtime.GetType().Name));
			}
		}
		if (module.ComponentCommands.Count > 0) {
			errors.Add(Loc.Format("ContractComponentCommands", run.Name, module.Name, module.ComponentCommands.Count));
		}
		foreach (var sub in module.SubModules) {
			CheckCommandContracts(sub, errors, run);
		}
	}

	private static InteractionService CreateService() {
		Interlocked.Increment(ref _createdServices);
		var client = DiscordGateway.Client ?? throw new InvalidOperationException(Loc.Get("DiscordClientNotInitialized"));
		var interactions = new InteractionService(client.Rest, new InteractionServiceConfig {
			AutoServiceScopes = false,
			DefaultRunMode = RunMode.Sync,
			LogLevel = LogSeverity.Warning,
		});
		interactions.Log += OnServiceLog;
		interactions.InteractionExecuted += OnExecuted;
		return interactions;
	}

	private static void RetireService(InteractionService service) {
		service.Log -= OnServiceLog;
		service.InteractionExecuted -= OnExecuted;
		service.Dispose();
		Interlocked.Increment(ref _retiredServices);
	}

	private static Task OnServiceLog(LogMessage message) {
		var text = Loc.Format("InteractionServiceLog", message.Severity, message.Message ?? Loc.Get("LogMessageNoText"));
		if (message.Exception != null) {
			Logger.Error(typeof(InteractionHost), message.Exception, text);
		} else if (message.Severity is LogSeverity.Error or LogSeverity.Critical) {
			Logger.Error(typeof(InteractionHost), text);
		} else {
			Logger.Info(typeof(InteractionHost), text);
		}
		return Task.CompletedTask;
	}

	private static Task OnExecuted(ICommandInfo? command, IInteractionContext? context, IResult result) {
		if (result == null || result.IsSuccess) return Task.CompletedTask;
		if (result.Error == InteractionCommandError.UnmetPrecondition) {
			if (context?.Interaction is { HasResponded: false } interaction) {
				_ = RespondUnavailableAsync(interaction, result.ErrorReason);
			}
			return Task.CompletedTask;
		}
		if (result is ExecuteResult { Exception: { } executedException }) {
			Logger.Error(typeof(InteractionHost), executedException, Loc.Format("CommandExecutionException", command?.Name ?? "?"));
		} else {
			Logger.Error(typeof(InteractionHost), Loc.Format("CommandExecutionFailed", command?.Name ?? "?", result.Error, result.ErrorReason));
		}
		return Task.CompletedTask;
	}

	private static async Task RespondUnavailableAsync(IDiscordInteraction interaction, string reason) {
		try {
			await interaction.RespondAsync(reason, ephemeral: true);
		} catch (Exception e) {
			Logger.Error(typeof(InteractionHost), e, Loc.Get("ModuleUnavailableNoticeSendFailed"));
		}
	}
}

public sealed record InteractionSnapshot(InteractionService Service, IReadOnlyList<LoadedModule> Modules);

public sealed record ModuleAttachResult(IReadOnlyList<LoadedModule> Valid, IReadOnlyList<LoadedModule> Invalid, IReadOnlyList<string> Errors);

public sealed record ServiceReleaseResult(bool Released, string? Reason);
