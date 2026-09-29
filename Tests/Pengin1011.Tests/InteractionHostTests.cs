using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Pengin1011.Core;
using Pengin1011.Core.Modules;
using Pengin1011.Modules.FakeModule;
using Pengin1011.Modules.FakeSecond;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class InteractionHostTests {
	private static void PrepareHost() {
		ModuleHostTests.ResetModules();
		HostExit.ResetForTest();
		ExitCoordinator.ResetForTest();
		DiscordGateway.ResetClientOverridesForTest();
		InteractionHost.ResetForTest();
		ModuleHost.StopTimeout = TimeSpan.FromMilliseconds(500);
		DiscordGateway.Init(new DiscordSocketClient());
		ModuleHost.LoadAll();
	}

	private static async Task AwaitInitsAsync(LoadedModule? skip = null) {
		foreach (var run in ModuleHost.Modules) {
			if (run == skip || run.InitTask == null) continue;
			try {
				await run.InitTask.WaitAsync(TimeSpan.FromSeconds(2));
			} catch (TimeoutException) {
			}
		}
	}

	private async Task<InteractionService> BootAsync() {
		PrepareHost();
		await InteractionHost.PublishInitialAsync();
		await AwaitInitsAsync();
		return InteractionHost.Current.Service;
	}

	private async Task<LoadedModule> BootWithGatedInitAsync(string moduleName, TaskCompletionSource gate) {
		PrepareHost();
		var run = ModuleHost.Modules.Single(module => module.Name == moduleName);
		ModuleProbe.SetRuntimeStatic(run, "InitGate", gate);
		await InteractionHost.PublishInitialAsync();
		await AwaitInitsAsync(run);
		await WaitForAsync(() => ModuleProbe.RuntimeStatic<TaskCompletionSource?>(run, "InitEntered") is not null, $"{moduleName} 未进入初始化");
		await ModuleProbe.RuntimeStatic<TaskCompletionSource>(run, "InitEntered").Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(ModuleState.Starting, run.State);
		Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
		return run;
	}

	private static LoadedModule Run(string name) {
		return ModuleHost.Modules.Single(module => module.Name == name);
	}

	private async Task TeardownAsync() {
		foreach (var run in ModuleHost.Modules) {
			await ModuleHost.StopAsync(run);
		}
		HostExit.ResetForTest();
		ExitCoordinator.ResetForTest();
		DiscordGateway.ResetClientOverridesForTest();
		InteractionHost.ResetForTest();
		ModuleHostTests.ResetModules();
	}

	private static async Task AwaitSyncTaskAsync() {
		var syncTask = InteractionHost.SyncTaskForTest;
		if (syncTask != null) {
			await syncTask.WaitAsync(TimeSpan.FromSeconds(5));
		}
	}

	private static async Task WaitForAsync(Func<bool> condition, string description) {
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (!condition() && DateTime.UtcNow < deadline) {
			await Task.Delay(25);
		}
		Assert.True(condition(), description);
	}

	private static async Task AwaitFinalStopAsync(LoadedModule run) {
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (run.FinalStopResult == null && DateTime.UtcNow < deadline) {
			await Task.Delay(25);
		}
		Assert.NotNull(run.FinalStopResult);
	}

	private static (IInteractionContext Context, object Interaction) MakeContext(string commandName) {
		var interaction = new FakeInteraction(commandName);
		return (new FakeInteractionContext(interaction), interaction);
	}

	[Fact]
	public async Task Startup_LegalModulesReadyAndCommandsExecute() {
		var service = await BootAsync();
		try {
			Assert.Equal(3, ModuleHost.Modules.Count);
			Assert.Equal(3, InteractionHost.Current.Modules.Count);
			foreach (var run in ModuleHost.Modules) {
				Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
			}

			var fakeRun = Run("FakeModule");
			var (context, _) = MakeContext("fakeping");
			await service.ExecuteCommandAsync(context, EmptyServiceProvider.Instance);
			Assert.Equal(1, ModuleProbe.CommandStaticInt(fakeRun, "FakeModule", "CommandCount"));
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task PublishInitial_BadModuleReleased_LegalModulesStillServe() {
		var bare = Path.Combine(AppContext.BaseDirectory, "module", "FakeBare.dll");
		File.Copy(Path.Combine(AppContext.BaseDirectory, "baremodule", "FakeBare.dll"), bare, true);
		PrepareHost();
		var retiredBefore = InteractionHost.RetiredServiceCountForTest;
		try {
			await InteractionHost.PublishInitialAsync();
			await AwaitInitsAsync();
			var bareRun = Run("FakeBare");
			Assert.Equal(ModuleState.Disabled, bareRun.State);
			Assert.Null(bareRun.InitTask);
			Assert.Equal(L.Get("StartupContractCheckFailed"), bareRun.DisabledReason);
			Assert.Equal(retiredBefore + 1, InteractionHost.RetiredServiceCountForTest);
			Assert.DoesNotContain(InteractionHost.Current.Modules, run => run.Name == "FakeBare");
			Assert.Equal(3, InteractionHost.Current.Modules.Count);
			foreach (var run in InteractionHost.Current.Modules) {
				Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
			}
		} finally {
			File.Delete(bare);
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task PublishInitial_AllModulesInvalid_EmptySnapshotWithoutInit() {
		PrepareHost();
		Assert.Throws<InvalidOperationException>(() => InteractionHost.Current);
		var moved = new List<(string From, string To)>();
		foreach (var name in new[] { "FakeModule", "FakeSecond", "FakeThird" }) {
			var from = Path.Combine(AppContext.BaseDirectory, "module", name + ".dll");
			var to = from + ".bak";
			File.Move(from, to);
			moved.Add((from, to));
		}
		var bare = Path.Combine(AppContext.BaseDirectory, "module", "FakeBare.dll");
		File.Copy(Path.Combine(AppContext.BaseDirectory, "baremodule", "FakeBare.dll"), bare, true);
		try {
			ModuleHostTests.ResetModules();
			ModuleHost.LoadAll();
			await InteractionHost.PublishInitialAsync();

			Assert.Empty(InteractionHost.Current.Modules);
			Assert.Empty(InteractionHost.Current.Service.SlashCommands);
			var bareRun = Assert.Single(ModuleHost.Modules);
			Assert.Equal("FakeBare", bareRun.Name);
			Assert.Equal(ModuleState.Disabled, bareRun.State);
			Assert.Null(bareRun.InitTask);
			Assert.True(DiscordGateway.Dispatcher!.Accepting);
		} finally {
			File.Delete(bare);
			foreach (var (from, to) in moved) {
				File.Move(to, from);
			}
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task PublishInitial_BeforeReady_DoNotInitializeOrAccept() {
		PrepareHost();
		try {
			Assert.False(DiscordGateway.Dispatcher!.Accepting);
			foreach (var run in ModuleHost.Modules) {
				Assert.Null(run.InitTask);
				Assert.Equal(ModuleState.Starting, run.State);
			}
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task Current_BeforePublish_ThrowsWithoutCreatingService() {
		PrepareHost();
		try {
			Assert.Null(InteractionHost.SnapshotForTest);
			Assert.Throws<InvalidOperationException>(() => InteractionHost.Current);
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task StartRuntime_AfterShutdown_NoNewInitialization() {
		PrepareHost();
		try {
			Assert.NotEmpty(ModuleHost.Modules);
			foreach (var run in ModuleHost.Modules) {
				Assert.Null(run.InitTask);
				Assert.Equal(0, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
			}

			InteractionHost.RequestShutdown();
			foreach (var run in ModuleHost.Modules) {
				ModuleHost.StartRuntime(run);
			}

			foreach (var run in ModuleHost.Modules) {
				Assert.Null(run.InitTask);
				Assert.Equal(0, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
			}
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task PublishInitial_ExitBetweenPublishAndInit_NoInitSideEffects() {
		PrepareHost();
		var barrierEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var barrierRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		InteractionHost.PublishBarrierForTest = () => {
			barrierEntered.TrySetResult();
			return barrierRelease.Task;
		};
		try {
			var publishTask = InteractionHost.PublishInitialAsync();
			await barrierEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

			InteractionHost.RequestShutdown();
			barrierRelease.SetResult();
			await publishTask.WaitAsync(TimeSpan.FromSeconds(10));

			Assert.NotNull(InteractionHost.SnapshotForTest);
			Assert.False(DiscordGateway.Dispatcher!.Accepting, "退出登记后接单必须保持关闭");
			foreach (var run in ModuleHost.Modules) {
				Assert.Null(run.InitTask);
				Assert.Equal(0, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
			}

			var report = await HostExit.StopAsync();
			Assert.True(report.Success, $"failures: {string.Join(";", report.Failures)}");
			Assert.Null(InteractionHost.SnapshotForTest);
			Assert.False(DiscordGateway.Dispatcher!.Accepting, "退出结束后不得发布替代服务或恢复接单");
			foreach (var run in ModuleHost.Modules) {
				Assert.Null(run.InitTask);
				Assert.Equal(0, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
			}
		} finally {
			InteractionHost.PublishBarrierForTest = null;
			barrierRelease.TrySetResult();
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task CONC_01_ConcurrentStartRuntimeWithExit_BoundedNoDeadlockAtMostOneInit() {
		PrepareHost();
		var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var runs = ModuleHost.Modules;
		try {
			var callers = new List<Task>();
			for (var i = 0; i < runs.Count * 4; i++) {
				var run = runs[i % runs.Count];
				callers.Add(Task.Run(async () => {
					await start.Task;
					ModuleHost.StartRuntime(run);
				}));
			}
			var exiter = Task.Run(async () => {
				await start.Task;
				InteractionHost.RequestShutdown();
			});
			start.SetResult();
			await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(10));
			await exiter.WaitAsync(TimeSpan.FromSeconds(10));

			foreach (var run in runs) {
				var init = run.InitTask;
				if (init != null) {
					await init.WaitAsync(TimeSpan.FromSeconds(5));
				}
				Assert.True(run.InitTask == null || run.InitTask == init, $"{run.Name} 初始化任务被替换");
				Assert.True(ModuleProbe.RuntimeStaticInt(run, "InitCount") <= 1, $"{run.Name} 初始化超过一次");
			}
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task G04_ReadySync_HoldsGate_SingleExecution() {
		await BootAsync();
		var syncCount = 0;
		var syncEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var syncHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		InteractionHost.SyncBodyForTest = ct => {
			Interlocked.Increment(ref syncCount);
			syncEntered.SetResult();
			return syncHold.Task;
		};
		try {
			InteractionHost.OnGatewayReady();
			await syncEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

			InteractionHost.OnGatewayReady();
			Assert.True(InteractionHost.SyncPendingForTest);

			syncHold.SetResult();
			await InteractionHost.SyncTaskForTest!.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(2, syncCount);
		} finally {
			syncHold.TrySetResult();
			InteractionHost.SyncBodyForTest = null;
			await AwaitSyncTaskAsync();
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task G05_MultipleReadyAndManualSync_ConcurrencyOne_IntentMerged() {
		await BootAsync();
		var concurrent = 0;
		var total = 0;
		var maxConcurrent = 0;
		var maxLock = new object();
		var phase = 0;
		var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var gate1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var gate2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		InteractionHost.SyncBodyForTest = async ct => {
			var current = Interlocked.Increment(ref concurrent);
			Interlocked.Increment(ref total);
			lock (maxLock) {
				if (current > maxConcurrent) maxConcurrent = current;
			}
			var entered = phase == 0 ? firstEntered : secondEntered;
			var gate = phase == 0 ? gate1.Task : gate2.Task;
			entered.SetResult();
			try {
				await gate.WaitAsync(ct);
			} finally {
				Interlocked.Decrement(ref concurrent);
			}
		};
		try {
			InteractionHost.OnGatewayReady();
			await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

			for (var i = 0; i < 5; i++) {
				InteractionHost.OnGatewayReady();
			}
			Assert.True(InteractionHost.SyncPendingForTest);

			var syncTask = InteractionHost.SyncAsync(CancellationToken.None);
			await Task.Delay(100);
			Assert.False(syncTask.IsCompleted, "手动 sync 必须等控制门闩（串行）");

			phase = 1;
			gate1.SetResult();
			await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(1, Volatile.Read(ref concurrent));

			gate2.SetResult();
			await InteractionHost.SyncTaskForTest!.WaitAsync(TimeSpan.FromSeconds(5));
			await syncTask;

			Assert.Equal(2, Volatile.Read(ref total));
			Assert.Equal(1, maxConcurrent);
			Assert.Equal(0, Volatile.Read(ref concurrent));
		} finally {
			gate1.TrySetResult();
			gate2.TrySetResult();
			InteractionHost.SyncBodyForTest = null;
			await AwaitSyncTaskAsync();
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task G06_QueuedControlOps_EndWithCancellation_CurrentNotTouched() {
		await BootAsync();
		var oldService = InteractionHost.Current.Service;
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		InteractionHost.SyncBodyForTest = ct => {
			entered.SetResult();
			return hold.Task;
		};
		try {
			var shutdownToken = InteractionHost.ShutdownToken;
			InteractionHost.OnGatewayReady();
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

			var syncTask = InteractionHost.SyncAsync(shutdownToken);
			await Task.Delay(100);
			Assert.False(syncTask.IsCompleted);

			InteractionHost.RequestShutdown();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => syncTask);
			Assert.Same(oldService, InteractionHost.Current.Service);

			hold.SetResult();
			await AwaitSyncTaskAsync();
		} finally {
			hold.TrySetResult();
			InteractionHost.SyncBodyForTest = null;
			await AwaitSyncTaskAsync();
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task GATE_01_TimeoutDoesNotReleaseAnotherHoldersLock() {
		await BootAsync();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		InteractionHost.SyncBodyForTest = ct => {
			entered.SetResult();
			return hold.Task;
		};
		try {
			InteractionHost.OnGatewayReady();
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(0, InteractionHost.ControlGateCountForTest);

			Assert.False(await InteractionHost.EnterControlSectionAsync(TimeSpan.FromMilliseconds(300)), "有持有者时必须报超时");
			Assert.Equal(0, InteractionHost.ControlGateCountForTest);
			Assert.False(await InteractionHost.EnterControlSectionAsync(TimeSpan.FromMilliseconds(200)), "第三个操作同样不能取得");
			Assert.Equal(0, InteractionHost.ControlGateCountForTest);

			hold.SetResult();
			await AwaitSyncTaskAsync();
			Assert.Equal(1, InteractionHost.ControlGateCountForTest);

			Assert.True(await InteractionHost.EnterControlSectionAsync(TimeSpan.FromSeconds(1)), "原持有者释放后应能正常取得");
			InteractionHost.LeaveControlSection();
			Assert.Equal(1, InteractionHost.ControlGateCountForTest);
		} finally {
			hold.TrySetResult();
			InteractionHost.SyncBodyForTest = null;
			await AwaitSyncTaskAsync();
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task GATE_02_NormalAcquireAndRelease_CountStaysExactlyOne() {
		await BootAsync();
		try {
			for (var round = 0; round < 3; round++) {
				Assert.Equal(1, InteractionHost.ControlGateCountForTest);
				Assert.True(await InteractionHost.EnterControlSectionAsync(TimeSpan.FromSeconds(1)));
				Assert.Equal(0, InteractionHost.ControlGateCountForTest);
				InteractionHost.LeaveControlSection();
				Assert.Equal(1, InteractionHost.ControlGateCountForTest);
			}
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task GATE_03_CancellationIsNotAnAcquire_HolderStillReleases() {
		await BootAsync();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		InteractionHost.SyncBodyForTest = ct => {
			entered.SetResult();
			return hold.Task;
		};
		try {
			InteractionHost.OnGatewayReady();
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

			using var cts = new CancellationTokenSource();
			var pendingSync = InteractionHost.SyncAsync(cts.Token);
			Assert.False(pendingSync.IsCompleted);
			cts.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingSync);
			Assert.Equal(0, InteractionHost.ControlGateCountForTest);

			InteractionHost.RequestShutdown();
			Assert.Equal(L.Get("FrameworkExiting"), await InteractionHost.SyncAsync(CancellationToken.None));
			Assert.Equal(0, InteractionHost.ControlGateCountForTest);

			hold.SetResult();
			await AwaitSyncTaskAsync();
			Assert.Equal(1, InteractionHost.ControlGateCountForTest);
		} finally {
			hold.TrySetResult();
			InteractionHost.SyncBodyForTest = null;
			await AwaitSyncTaskAsync();
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task GATE_04_ControlGateTimeoutEntersRealExitReport() {
		await BootAsync();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		InteractionHost.SyncBodyForTest = ct => {
			entered.SetResult();
			return hold.Task;
		};
		ExitCoordinator.ControlSectionBudget = TimeSpan.FromMilliseconds(300);
		try {
			InteractionHost.OnGatewayReady();
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

			var report = await new ExitCoordinator().RunAsync();

			Assert.False(report.Success);
			Assert.Contains(report.Failures, failure => failure.Contains(L.Prefix("ControlSectionTimeout")));
			Assert.Equal(0, InteractionHost.ControlGateCountForTest);
		} finally {
			hold.TrySetResult();
			InteractionHost.SyncBodyForTest = null;
			await AwaitSyncTaskAsync();
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task G08_ExitDuringInitialization_SingleInitNeverReady() {
		var initGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var gated = await BootWithGatedInitAsync("FakeModule", initGate);
		try {
			var report = await HostExit.StopAsync();

			Assert.True(report.Success, $"failures: {string.Join(";", report.Failures)}");
			Assert.NotEqual(ModuleState.Ready, gated.State);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(gated, "InitCount"));
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(gated, "StopCount"));

			initGate.SetResult();
			await AwaitFinalStopAsync(gated);
			Assert.NotEqual(ModuleState.Ready, gated.State);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(gated, "InitCount"));
		} finally {
			initGate.TrySetResult();
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task START_01_OneModuleStarting_OthersServeCommands() {
		var initGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var gated = await BootWithGatedInitAsync("FakeModule", initGate);
		try {
			Assert.Contains(gated, InteractionHost.Current.Modules);
			Assert.Equal(ModuleState.Starting, gated.State);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(gated, "InitCount"));

			var third = Run("FakeThird");
			Assert.Equal(ModuleState.Ready, third.State);
			var (context, _) = MakeContext("fakethird");
			await InteractionHost.Current.Service.ExecuteCommandAsync(context, EmptyServiceProvider.Instance);
			Assert.Equal(1, ModuleProbe.CommandStaticInt(third, "FakeThird", "CommandCount"));
		} finally {
			initGate.TrySetResult();
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task START_02_StartingModuleCommandsBlocked_ThenReadyServes() {
		var initGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var gated = await BootWithGatedInitAsync("FakeModule", initGate);
		try {
			var (blockedContext, blockedInteraction) = MakeContext("fakeping");
			await InteractionHost.Current.Service.ExecuteCommandAsync(blockedContext, EmptyServiceProvider.Instance);
			Assert.Equal(0, ModuleProbe.CommandStaticInt(gated, "FakeModule", "CommandCount"));
			Assert.Contains((IReadOnlyList<string>)blockedInteraction.GetType().GetProperty("Responses")!.GetValue(blockedInteraction)!, response => response.Contains(L.Get("ModuleUnavailable")));

			initGate.SetResult();
			await ModuleHostTests.WaitStateAsync(gated, ModuleState.Ready);
			ModuleProbe.SetCommandStatic(gated, "FakeModule", "CommandCount", 0);
			var (context, _) = MakeContext("fakeping");
			await InteractionHost.Current.Service.ExecuteCommandAsync(context, EmptyServiceProvider.Instance);
			Assert.Equal(1, ModuleProbe.CommandStaticInt(gated, "FakeModule", "CommandCount"));
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(gated, "InitCount"));
		} finally {
			initGate.TrySetResult();
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task START_03_InitFailureModuleDisabled_RecordKeptAndCommandBlocked() {
		PrepareHost();
		var gated = Run("FakeThird");
		ModuleProbe.SetRuntimeStatic(gated, "OnInitFailure", (Func<Exception>)(() => new InvalidOperationException("START-03 初始化失败")));
		try {
			await InteractionHost.PublishInitialAsync();
			await AwaitFinalStopAsync(gated);
			Assert.Equal(ModuleState.Disabled, gated.State);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(gated, "InitCount"));
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(gated, "StopCount"));
			Assert.Contains(gated, ModuleHost.Modules);

			var (context, interaction) = MakeContext("fakethird");
			await InteractionHost.Current.Service.ExecuteCommandAsync(context, EmptyServiceProvider.Instance);
			Assert.Equal(0, ModuleProbe.CommandStaticInt(gated, "FakeThird", "CommandCount"));
			Assert.Contains((IReadOnlyList<string>)interaction.GetType().GetProperty("Responses")!.GetValue(interaction)!, response => response.Contains(L.Get("ModuleUnavailable")));
		} finally {
			await TeardownAsync();
		}
	}
}