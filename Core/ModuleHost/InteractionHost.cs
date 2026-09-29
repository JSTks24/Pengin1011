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

	private static readonly SemaphoreSlim ControlGate = new(1, 1);
	private static readonly object ExitSync = new();
	private static readonly object SyncGate = new();
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
			lock (ExitSync) {
				return _exiting;
			}
		}
	}

	public static InteractionSnapshot Current => _current ?? throw new InvalidOperationException(Localizer.Get("InteractionServiceNotInitialized"));

	public static void RequestShutdown() {
		lock (ExitSync) {
			_exiting = true;
		}
		DiscordGateway.RequestExitClose();
		try {
			_shutdownCts.Cancel();
		} catch (ObjectDisposedException) {
		}
	}

	internal static bool TryBeginModuleInitialization(LoadedModule run) {
		lock (ExitSync) {
			if (_exiting) return false;
			return run.TryBeginInit(() => run.Runtime.InitializeAsync(run.Lifecycle.Token)) != null;
		}
	}

	internal static bool TryMarkModuleReady(LoadedModule run) {
		lock (ExitSync) {
			if (_exiting) return false;
			return run.TryMarkReady();
		}
	}

	public static async Task PublishInitialAsync() {
		await ControlGate.WaitAsync(ShutdownToken);
		try {
			lock (ExitSync) {
				if (_exiting || _current != null) {
					return;
				}
			}
			var runs = ModuleHost.Modules;
			var service = CreateService();
			var attach = await AttachAsync(service, runs);
			var routable = attach.Valid;
			foreach (var error in attach.Errors) {
				Logger.Error(typeof(InteractionHost), Localizer.Format("StartupAttachContractError", error));
			}
			if (attach.Invalid.Count > 0) {
				RetireService(service);
				service = CreateService();
				var rebuilt = await AttachAsync(service, attach.Valid);
				foreach (var error in rebuilt.Errors) {
					Logger.Error(typeof(InteractionHost), Localizer.Format("StartupRebuildContractError", error));
				}
				routable = rebuilt.Valid;
				foreach (var run in attach.Invalid.Concat(rebuilt.Invalid)) {
					run.MarkInitFailed(Localizer.Get("StartupContractCheckFailed"));
				}
			}
			var published = false;
			lock (ExitSync) {
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
			ControlGate.Release();
		}
	}

	public static async Task ExecuteAsync(SocketInteraction interaction, CancellationToken ct) {
		if (interaction is SocketMessageComponent or SocketModal) return;
		var snapshot = Current;
		var context = new SocketInteractionContext(DiscordGateway.Client!, interaction);
		await snapshot.Service.ExecuteCommandAsync(context, EmptyServiceProvider.Instance);
	}

	internal static void OnGatewayReady() {
		lock (SyncGate) {
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
			lock (SyncGate) {
				return _syncTask;
			}
		}
	}

	internal static bool SyncPendingForTest {
		get {
			lock (SyncGate) {
				return _syncPending;
			}
		}
	}

	internal static int ControlGateCountForTest => ControlGate.CurrentCount;

	internal static InteractionSnapshot? SnapshotForTest => _current;

	internal static int RetiredServiceCountForTest => Volatile.Read(ref _retiredServices);

	internal static int CreatedServiceCountForTest => Volatile.Read(ref _createdServices);

	private static async Task RunReadySyncAsync() {
		while (true) {
			try {
				using var linked = CancellationTokenSource.CreateLinkedTokenSource(CurrentShutdownToken());
				await ControlGate.WaitAsync(linked.Token);
				try {
					if (!Exiting) {
						await SyncBodyAsync(linked.Token);
					}
				} finally {
					ControlGate.Release();
				}
			} catch (OperationCanceledException) {
			} catch (Exception e) {
				Logger.Error(typeof(InteractionHost), e, Localizer.Get("ReadySyncFailed"));
			}
			lock (SyncGate) {
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
			Logger.Info(typeof(InteractionHost), Localizer.Get("CommandGlobalRegisterCompleted"));
			return null;
		} catch (Exception e) {
			Logger.Error(typeof(InteractionHost), e, Localizer.Get("CommandGlobalRegisterFailed"));
			return e.Message;
		}
	}

	public static async Task<string?> SyncAsync(CancellationToken ct) {
		if (Exiting) return Localizer.Get("FrameworkExiting");
		await ControlGate.WaitAsync(ct);
		try {
			if (Exiting) return Localizer.Get("FrameworkExiting");
			return await RegisterCommandsSafeAsync();
		} finally {
			ControlGate.Release();
		}
	}

	internal static async Task<bool> EnterControlSectionAsync(TimeSpan budget) {
		return await ControlGate.WaitAsync(budget);
	}

	internal static void LeaveControlSection() {
		ControlGate.Release();
	}

	internal static void ResetForTest() {
		InteractionSnapshot? snapshot;
		lock (ExitSync) {
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
		lock (SyncGate) {
			syncTask = _syncTask;
		}
		if (syncTask != null && !syncTask.IsCompleted) {
			try {
				await syncTask.WaitAsync(SyncStopWaitForTest);
			} catch (TimeoutException) {
				var reason = Localizer.Format("ReadySyncStopTimeout", SyncStopWaitForTest.TotalSeconds);
				Logger.Error(typeof(InteractionHost), reason);
				return new ServiceReleaseResult(false, reason);
			} catch (OperationCanceledException) {
			}
		}
		InteractionSnapshot? snapshot;
		lock (ExitSync) {
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
					Logger.Info(typeof(InteractionHost), Localizer.Format("ModuleCommandsAttached", run.Name, built.Count()));
				} else {
					invalid.Add(run);
					errors.AddRange(runErrors);
					Logger.Error(typeof(InteractionHost), Localizer.Format("ModuleAttachContractFailed", run.Name, runErrors.Count));
				}
			} catch (Exception e) {
				Logger.Error(typeof(InteractionHost), e, Localizer.Format("ModuleAttachFailed", run.Name));
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
				errors.Add(Localizer.Format("ContractRunModeOverride", run.Name, command.Name, command.RunMode));
			}
		}
		var attributes = module.Preconditions.OfType<ModuleAvailabilityAttribute>().ToList();
		if (!module.IsSubModule && attributes.Count == 0) {
			errors.Add(Localizer.Format("ContractMissingAvailability", run.Name, module.Name));
		}
		foreach (var attribute in attributes) {
			if (attribute.RuntimeType != run.Runtime.GetType()) {
				errors.Add(Localizer.Format("ContractAvailabilityMismatch", run.Name, module.Name, attribute.RuntimeType.Name, run.Runtime.GetType().Name));
			}
		}
		if (module.ComponentCommands.Count > 0) {
			errors.Add(Localizer.Format("ContractComponentCommands", run.Name, module.Name, module.ComponentCommands.Count));
		}
		foreach (var sub in module.SubModules) {
			CheckCommandContracts(sub, errors, run);
		}
	}

	private static InteractionService CreateService() {
		Interlocked.Increment(ref _createdServices);
		var client = DiscordGateway.Client ?? throw new InvalidOperationException(Localizer.Get("DiscordClientNotInitialized"));
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
		var text = Localizer.Format("InteractionServiceLog", message.Severity, message.Message ?? Localizer.Get("LogMessageNoText"));
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
			Logger.Error(typeof(InteractionHost), executedException, Localizer.Format("CommandExecutionException", command?.Name ?? "?"));
		} else {
			Logger.Error(typeof(InteractionHost), Localizer.Format("CommandExecutionFailed", command?.Name ?? "?", result.Error, result.ErrorReason));
		}
		return Task.CompletedTask;
	}

	private static async Task RespondUnavailableAsync(IDiscordInteraction interaction, string reason) {
		try {
			await interaction.RespondAsync(reason, ephemeral: true);
		} catch (Exception e) {
			Logger.Error(typeof(InteractionHost), e, Localizer.Get("ModuleUnavailableNoticeSendFailed"));
		}
	}
}

public sealed record InteractionSnapshot(InteractionService Service, IReadOnlyList<LoadedModule> Modules);

public sealed record ModuleAttachResult(IReadOnlyList<LoadedModule> Valid, IReadOnlyList<LoadedModule> Invalid, IReadOnlyList<string> Errors);

public sealed record ServiceReleaseResult(bool Released, string? Reason);
