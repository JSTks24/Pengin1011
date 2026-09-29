using Discord.WebSocket;
using Pengin1011.Core;
using Pengin1011.Core.Logging;
using Pengin1011.Core.Modules;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ModuleLifecycleTests {
	private static readonly TimeSpan ShortWait = TimeSpan.FromMilliseconds(500);
	private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

	private readonly TimeSpan _stopTimeout = ModuleHost.StopTimeout;
	private readonly TimeSpan _cleanupBudget = ModuleHost.CleanupBudget;

	private static string ModuleDll(string name = "FakeModule") {
		var path = Path.Combine(AppContext.BaseDirectory, "module", name + ".dll");
		Assert.True(File.Exists(path), $"{name}.dll 缺失：{path}（测试资产应由构建复制保障）");
		return path;
	}

	private static LoadedModule NewRun() {
		return ModuleHost.LoadOne("FakeModule", ModuleDll());
	}

	private static T Static<T>(LoadedModule run, string field) {
		return ModuleProbe.RuntimeStatic<T>(run, field);
	}

	private static void SetStatic(LoadedModule run, string field, object? value) {
		ModuleProbe.SetRuntimeStatic(run, field, value);
	}

	private static void Release(LoadedModule run, string field) {
		var info = ModuleProbe.RuntimeType(run).GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
		(info?.GetValue(null) as TaskCompletionSource)?.TrySetResult();
	}

	private static TaskCompletionSource NewGate() {
		return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	private static Task Entered(LoadedModule run, string field) {
		return Static<TaskCompletionSource>(run, field).Task.WaitAsync(HandshakeTimeout);
	}

	private static async Task<string> ReadLogWhenContainsAsync(string path, string expected) {
		var deadline = DateTime.UtcNow.AddSeconds(HandshakeTimeout.TotalSeconds);
		var text = "";
		while (DateTime.UtcNow < deadline) {
			text = File.Exists(path) ? File.ReadAllText(path) : "";
			if (text.Contains(expected)) return text;
			await Task.Delay(25);
		}
		return text;
	}

	private static async Task DrainAsync(LoadedModule run) {
		ModuleHost.StopTimeout = TimeSpan.FromSeconds(10);
		ModuleHost.CleanupBudget = TimeSpan.FromSeconds(10);
		Release(run, "StopSyncGate");
		Release(run, "InitGate");
		Release(run, "StopGate");
		await ModuleHost.StopAsync(run);
	}

	private async Task RestoreAndResetAsync(params LoadedModule[] runs) {
		foreach (var run in runs) {
			await DrainAsync(run);
		}
		ModuleHost.StopTimeout = _stopTimeout;
		ModuleHost.CleanupBudget = _cleanupBudget;
		ModuleHostTests.ResetModules();
	}

	[Fact]
	public async Task L01_StopWhileInitAtGate_PendingTimeoutThenCleanAfterReleases() {
		ModuleHostTests.ResetModules();
		var run = NewRun();
		ModuleHost.Adopt(run);
		SetStatic(run, "InitGate", NewGate());
		SetStatic(run, "StopGate", NewGate());
		SetStatic(run, "InitEntered", NewGate());
		SetStatic(run, "StopEntered", NewGate());
		SetStatic(run, "InitIgnoreCancel", true);
		ModuleHost.StopTimeout = ShortWait;
		try {
			ModuleHost.StartRuntime(run);
			await Entered(run, "InitEntered");

			var pending = ModuleHost.StopAsync(run);
			Assert.Equal(ModuleState.Stopping, run.State);

			var timedOut = await pending;
			Assert.Equal(ModuleStopOutcome.PendingTimeout, timedOut.Outcome);
			Assert.Equal(0, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
			Assert.Contains(run, ModuleHost.Modules);
			Assert.Same(run, ModuleRegistry.RunOf(run.Assembly));

			Release(run, "InitGate");
			await Entered(run, "StopEntered");
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));

			Release(run, "StopGate");
			var stop = await ModuleHost.StopAsync(run);
			Assert.Equal(ModuleStopOutcome.Clean, stop.Outcome);
			Assert.Equal(ModuleState.Stopped, run.State);
			Assert.Equal(new[] { "init-entered", "init-finished", "stop-entered", "stop-finished" }, Static<List<string>>(run, "Events"));
		} finally {
			await RestoreAndResetAsync(run);
		}
	}

	[Fact]
	public async Task L02_InitIgnoresCancel_PendingTimeoutKeepsRecordThenLateSingleCleanup() {
		ModuleHostTests.ResetModules();
		var run = NewRun();
		ModuleHost.Adopt(run);
		SetStatic(run, "InitGate", NewGate());
		SetStatic(run, "InitEntered", NewGate());
		SetStatic(run, "InitIgnoreCancel", true);
		ModuleHost.StopTimeout = ShortWait;
		try {
			ModuleHost.StartRuntime(run);
			await Entered(run, "InitEntered");

			var timedOut = await ModuleHost.StopAsync(run);
			Assert.Equal(ModuleStopOutcome.PendingTimeout, timedOut.Outcome);
			Assert.Contains(run, ModuleHost.Modules);
			Assert.False(run.InitTask!.IsCompleted);

			Release(run, "InitGate");
			var stop = await ModuleHost.StopAsync(run);
			Assert.Equal(ModuleStopOutcome.Clean, stop.Outcome);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
			Assert.Equal(ModuleState.Stopped, run.State);
		} finally {
			await RestoreAndResetAsync(run);
		}
	}

	[Fact]
	public async Task L03_InitFailureWithExternalStop_SingleCleanupDistinctResponsibilityLogs() {
		ModuleHostTests.ResetModules();
		var run = NewRun();
		ModuleHost.Adopt(run);
		var logPath = Path.Combine(Path.GetTempPath(), $"qq1011_lc_{Guid.NewGuid():N}", "error.log");
		Logger.SetLogPathForTest(logPath);
		SetStatic(run, "OnInitFailure", (Func<Exception>)(() => new InvalidOperationException("初始化故障-测试")));
		SetStatic(run, "OnStopFailure", (Func<Exception>)(() => new InvalidOperationException("清理故障-测试")));
		try {
			ModuleHost.StartRuntime(run);
			var stop = await ModuleHost.StopAsync(run);

			Assert.Equal(ModuleStopOutcome.Failed, stop.Outcome);
			Assert.Contains("清理故障-测试", stop.Reason);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
			Assert.True(run.InitTask!.IsCompleted);
			Assert.Equal(ModuleState.Disabled, run.State);

			var log = await ReadLogWhenContainsAsync(logPath, L.Prefix("ModuleInitFailed"));
			Assert.Contains(L.Prefix("ModuleInitFailed"), log);
			Assert.Contains("初始化故障-测试", log);
			Assert.Contains(L.Prefix("ModuleCleanupFailed"), log);
		} finally {
			Logger.SetLogPathForTest(null);
			await RestoreAndResetAsync(run);
		}
	}

	[Fact]
	public async Task L04_ConcurrentStopRequests_SingleCleanupIdenticalResults() {
		ModuleHostTests.ResetModules();
		var run = NewRun();
		ModuleHost.Adopt(run);
		SetStatic(run, "StopGate", NewGate());
		SetStatic(run, "StopEntered", NewGate());
		try {
			ModuleHost.StartRuntime(run);
			await ModuleHostTests.WaitStateAsync(run, ModuleState.Ready);

			var first = ModuleHost.StopAsync(run);
			await Entered(run, "StopEntered");
			var second = ModuleHost.StopAsync(run);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));

			Release(run, "StopGate");
			var r1 = await first;
			var r2 = await second;
			Assert.Equal(ModuleStopOutcome.Clean, r1.Outcome);
			Assert.Equal(r1.Outcome, r2.Outcome);
			Assert.Equal(r1.Reason, r2.Reason);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
			Assert.Equal(ModuleState.Stopped, run.State);
		} finally {
			await RestoreAndResetAsync(run);
		}
	}

	[Fact]
	public async Task L05_CleanupThrows_RepeatRequestNeverCleanNoRetry() {
		ModuleHostTests.ResetModules();
		var run = NewRun();
		ModuleHost.Adopt(run);
		SetStatic(run, "OnStopFailure", (Func<Exception>)(() => new InvalidOperationException("清理故障-测试")));
		try {
			ModuleHost.StartRuntime(run);
			await ModuleHostTests.WaitStateAsync(run, ModuleState.Ready);

			var first = await ModuleHost.StopAsync(run);
			var second = await ModuleHost.StopAsync(run);

			Assert.Equal(ModuleStopOutcome.Failed, first.Outcome);
			Assert.False(first.Clean);
			Assert.False(second.Clean);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
			Assert.Equal(ModuleState.Disabled, run.State);
		} finally {
			await RestoreAndResetAsync(run);
		}
	}

	[Fact]
	public async Task L06a_CleanupEntryTokenAlive_LastWriteCompletes() {
		ModuleHostTests.ResetModules();
		var run = NewRun();
		ModuleHost.Adopt(run);
		SetStatic(run, "StopGate", NewGate());
		SetStatic(run, "StopEntered", NewGate());
		try {
			ModuleHost.StartRuntime(run);
			await ModuleHostTests.WaitStateAsync(run, ModuleState.Ready);

			var stop = ModuleHost.StopAsync(run);
			await Entered(run, "StopEntered");
			Assert.False(Static<bool>(run, "StopEntryTokenCancelled"));

			Release(run, "StopGate");
			var result = await stop;
			Assert.Equal(ModuleStopOutcome.Clean, result.Outcome);
			Assert.False(Static<bool>(run, "StopEntryTokenCancelled"));
			Assert.Equal(new[] { "init-entered", "init-finished", "stop-entered", "stop-finished" }, Static<List<string>>(run, "Events"));
		} finally {
			await RestoreAndResetAsync(run);
		}
	}

	[Fact]
	public async Task L06b_CleanupBudgetExpiry_CancelsTokenAfterEntry() {
		ModuleHostTests.ResetModules();
		var run = NewRun();
		ModuleHost.Adopt(run);
		SetStatic(run, "StopGate", NewGate());
		SetStatic(run, "StopEntered", NewGate());
		ModuleHost.CleanupBudget = TimeSpan.FromMilliseconds(300);
		try {
			ModuleHost.StartRuntime(run);
			await ModuleHostTests.WaitStateAsync(run, ModuleState.Ready);

			var stop = ModuleHost.StopAsync(run);
			await Entered(run, "StopEntered");
			Assert.False(Static<bool>(run, "StopEntryTokenCancelled"));

			var result = await stop;
			Assert.Equal(ModuleStopOutcome.Failed, result.Outcome);
			Assert.Contains(L.Prefix("CleanupBudgetExceededDetail"), result.Reason);
			Assert.False(Static<bool>(run, "StopEntryTokenCancelled"));
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
			Assert.Equal(ModuleState.Disabled, run.State);
		} finally {
			await RestoreAndResetAsync(run);
		}
	}

	[Fact]
	public async Task L07_StoppingModule_NeverReadyAndEventSkipped() {
		ModuleHostTests.ResetModules();
		DiscordGateway.Init(new DiscordSocketClient());
		DiscordGateway.OpenAccepting();
		var run = NewRun();
		ModuleHost.Adopt(run);
		SetStatic(run, "EnableBackgroundWork", true);
		try {
			ModuleHost.StartRuntime(run);
			await ModuleHostTests.WaitStateAsync(run, ModuleState.Ready);

			await DiscordGateway.DispatchMessageForTest(null);
			await DiscordGateway.DrainWorkAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "BackgroundEventCount"));

			SetStatic(run, "StopSyncGate", NewGate());
			SetStatic(run, "StopEntered", NewGate());
			var pending = ModuleHost.StopAsync(run);
			await Entered(run, "StopEntered");
			await ModuleHostTests.WaitStateAsync(run, ModuleState.Stopping);
			Assert.NotEqual(ModuleState.Ready, run.State);

			await DiscordGateway.DispatchMessageForTest(null);
			await DiscordGateway.DrainWorkAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "BackgroundEventCount"));

			Release(run, "StopSyncGate");
			var stop = await pending;
			Assert.Equal(ModuleStopOutcome.Clean, stop.Outcome);
			Assert.Equal(ModuleState.Stopped, run.State);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));

			await DiscordGateway.DispatchMessageForTest(null);
			await DiscordGateway.DrainWorkAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "BackgroundEventCount"));
		} finally {
			await RestoreAndResetAsync(run);
			DiscordGateway.DetachForTest();
		}
	}

	[Fact]
	public async Task L08a_TimeoutThenLateFailure_LateObserverKeepsFailed() {
		ModuleHostTests.ResetModules();
		var run = NewRun();
		ModuleHost.Adopt(run);
		SetStatic(run, "StopGate", NewGate());
		SetStatic(run, "StopEntered", NewGate());
		SetStatic(run, "OnStopFailure", (Func<Exception>)(() => new InvalidOperationException("清理故障-测试")));
		ModuleHost.StopTimeout = ShortWait;
		try {
			ModuleHost.StartRuntime(run);
			await ModuleHostTests.WaitStateAsync(run, ModuleState.Ready);

			var timedOut = await ModuleHost.StopAsync(run);
			Assert.Equal(ModuleStopOutcome.PendingTimeout, timedOut.Outcome);

			Release(run, "StopGate");
			var late = await ModuleHost.StopAsync(run);
			Assert.Equal(ModuleStopOutcome.Failed, late.Outcome);
			Assert.False(late.Clean);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
			Assert.Equal(ModuleState.Disabled, run.State);
			Assert.Equal(ModuleStopOutcome.Failed, run.FinalStopResult?.Outcome);
		} finally {
			await RestoreAndResetAsync(run);
		}
	}

	[Fact]
	public async Task L08b_TimeoutThenLateSuccess_StillSingleCleanupNoAutoReload() {
		ModuleHostTests.ResetModules();
		var run = NewRun();
		ModuleHost.Adopt(run);
		SetStatic(run, "StopGate", NewGate());
		SetStatic(run, "StopEntered", NewGate());
		ModuleHost.StopTimeout = ShortWait;
		try {
			ModuleHost.StartRuntime(run);
			await ModuleHostTests.WaitStateAsync(run, ModuleState.Ready);

			var timedOut = await ModuleHost.StopAsync(run);
			Assert.Equal(ModuleStopOutcome.PendingTimeout, timedOut.Outcome);

			Release(run, "StopGate");
			var late = await ModuleHost.StopAsync(run);
			Assert.Equal(ModuleStopOutcome.Clean, late.Outcome);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
			Assert.Equal(ModuleState.Stopped, run.State);
			Assert.True(run.FinalStopResult?.Clean);
		} finally {
			await RestoreAndResetAsync(run);
		}
	}

	[Fact]
	public async Task L09_DoubleStartRuntime_SyncHangCleanup_TimesOutAndKeepsTask() {
		ModuleHostTests.ResetModules();
		var run = NewRun();
		ModuleHost.Adopt(run);
		SetStatic(run, "StopSyncGate", NewGate());
		SetStatic(run, "StopEntered", NewGate());
		ModuleHost.StopTimeout = ShortWait;
		try {
			ModuleHost.StartRuntime(run);
			ModuleHost.StartRuntime(run);
			ModuleHost.StartRuntime(run);
			await ModuleHostTests.WaitStateAsync(run, ModuleState.Ready);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));

			var pending = ModuleHost.StopAsync(run);
			await Entered(run, "StopEntered");
			var timedOut = await pending;
			Assert.Equal(ModuleStopOutcome.PendingTimeout, timedOut.Outcome);
			Assert.Contains(run, ModuleHost.Modules);
			Assert.Null(run.FinalStopResult);

			Release(run, "StopSyncGate");
			var final = await ModuleHost.StopAsync(run);
			Assert.Equal(ModuleStopOutcome.Clean, final.Outcome);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
			Assert.Equal(ModuleState.Stopped, run.State);
		} finally {
			await RestoreAndResetAsync(run);
		}
	}

	[Fact]
	public async Task L10_StoppingStoppedNeverStarted_StartRuntimeAdmitsNothing() {
		ModuleHostTests.ResetModules();
		var run = NewRun();
		var fresh = ModuleHost.LoadOne("FakeSecond", ModuleDll("FakeSecond"));
		ModuleHost.Adopt(run);
		ModuleHost.Adopt(fresh);
		SetStatic(run, "InitGate", NewGate());
		SetStatic(run, "InitEntered", NewGate());
		SetStatic(run, "InitIgnoreCancel", true);
		ModuleHost.StopTimeout = ShortWait;
		try {
			ModuleHost.StartRuntime(run);
			await Entered(run, "InitEntered");
			var initTask = run.InitTask;
			Assert.NotNull(initTask);

			var pending = ModuleHost.StopAsync(run);
			Assert.Equal(ModuleState.Stopping, run.State);
			ModuleHost.StartRuntime(run);
			Assert.Same(initTask, run.InitTask);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));

			Release(run, "InitGate");
			var stop = await pending;
			Assert.Equal(ModuleStopOutcome.Clean, stop.Outcome);
			Assert.Equal(ModuleState.Stopped, run.State);
			ModuleHost.StartRuntime(run);
			Assert.Same(initTask, run.InitTask);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
			Assert.Equal(ModuleState.Stopped, run.State);

			var freshStop = await ModuleHost.StopAsync(fresh);
			Assert.Equal(ModuleStopOutcome.Clean, freshStop.Outcome);
			Assert.Equal(ModuleState.Stopped, fresh.State);
			Assert.Null(fresh.InitTask);
			ModuleHost.StartRuntime(fresh);
			Assert.Null(fresh.InitTask);
			Assert.Equal(0, ModuleProbe.RuntimeStaticInt(fresh, "InitCount"));
			Assert.Equal(ModuleState.Stopped, fresh.State);
		} finally {
			await RestoreAndResetAsync(run, fresh);
		}
	}

	[Fact]
	public async Task Lifecycle_StopDuringResponsiveInit_CleanStopEndsStoppedWithoutInitFailureLabel() {
		ModuleHostTests.ResetModules();
		var run = NewRun();
		ModuleHost.Adopt(run);
		SetStatic(run, "InitGate", NewGate());
		SetStatic(run, "InitEntered", NewGate());
		SetStatic(run, "StopGate", NewGate());
		SetStatic(run, "StopEntered", NewGate());
		ModuleHost.StopTimeout = ShortWait;
		try {
			ModuleHost.StartRuntime(run);
			await Entered(run, "InitEntered");

			var pending = ModuleHost.StopAsync(run);
			var timedOut = await pending;
			Assert.Equal(ModuleStopOutcome.PendingTimeout, timedOut.Outcome);

			await Entered(run, "StopEntered");
			Assert.True(run.InitTask!.IsCompleted);

			Release(run, "StopGate");
			var stop = await ModuleHost.StopAsync(run);
			Assert.Equal(ModuleStopOutcome.Clean, stop.Outcome);
			Assert.Equal(ModuleState.Stopped, run.State);
			Assert.Null(run.DisabledReason);
		} finally {
			await RestoreAndResetAsync(run);
		}
	}
}