using Discord.WebSocket;
using Pengin1011.Core;
using Pengin1011.Core.Logging;
using Pengin1011.Core.Modules;
using Pengin1011.Modules.FakeModule;
using Pengin1011.Services.Discord;

using Pengin1011.Modules.FakeSecond;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class GatewayDispatchTests : IDisposable {
	public GatewayDispatchTests() {
		DiscordGateway.Init(new DiscordSocketClient());
		DiscordGateway.OpenAccepting();
	}

	public void Dispose() {
		DiscordGateway.DetachForTest();
	}

	private async Task<LoadedModule> BootFakeModuleAsync(Action<LoadedModule>? beforeInit = null) {
		ModuleHostTests.ResetModules();
		InteractionHost.ResetForTest();
		DiscordGateway.Init(new DiscordSocketClient());
		ModuleHost.LoadAll();
		if (beforeInit != null) {
			foreach (var run in ModuleHost.Modules) {
				beforeInit(run);
			}
		}
		await InteractionHost.PublishInitialAsync();
		return ModuleHost.Modules.Single(module => module.Name == "FakeModule");
	}

	private async Task TeardownAsync() {
		foreach (var run in ModuleHost.Modules) {
			await ModuleHost.StopAsync(run);
		}
		InteractionHost.ResetForTest();
		ModuleHostTests.ResetModules();
	}

	private async Task DispatchAndCountAsync(LoadedModule run, int expected) {
		await DiscordGateway.DispatchMessageForTest(null);
		await DiscordGateway.DrainWorkAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(expected, ModuleProbe.RuntimeStaticInt(run, "BackgroundEventCount"));
	}

	[Fact]
	public void PublicRegistrationsRequireAModuleOwner() {
		var registrations = typeof(DiscordGateway).GetMethods().Where(method => method.Name.StartsWith("Subscribe", StringComparison.Ordinal))
			.Concat(typeof(Components).GetMethods().Where(method => method.Name == "Register"));
		Assert.All(registrations, method => Assert.Equal(typeof(LoadedModule), method.GetParameters()[0].ParameterType));
		Assert.Throws<ArgumentNullException>(() => Components.Register(null!, "probe", (_, _) => Task.CompletedTask));
		Assert.Throws<ArgumentNullException>(() => Components.Register(null!, "probe", 1, null, (_, _, _) => Task.CompletedTask));
		Assert.Throws<ArgumentNullException>(() => DiscordGateway.SubscribeMessage(null!, (_, _) => Task.CompletedTask));
		Assert.Throws<ArgumentNullException>(() => DiscordGateway.SubscribeReady(null!, _ => Task.CompletedTask));
		Assert.Throws<ArgumentNullException>(() => DiscordGateway.SubscribeGuildAvailable(null!, (_, _) => Task.CompletedTask));
		Assert.Throws<ArgumentNullException>(() => DiscordGateway.SubscribeGuildJoined(null!, (_, _) => Task.CompletedTask));
		Assert.Throws<ArgumentNullException>(() => DiscordGateway.SubscribeInteraction(null!, (_, _) => Task.CompletedTask));
		Assert.Throws<ArgumentNullException>(() => DiscordGateway.SubscribeButton(null!, (_, _) => Task.CompletedTask));
		Assert.Throws<ArgumentNullException>(() => DiscordGateway.SubscribeSelectMenu(null!, (_, _) => Task.CompletedTask));
		Assert.Throws<ArgumentNullException>(() => DiscordGateway.SubscribeModal(null!, (_, _) => Task.CompletedTask));
	}

	[Fact]
	public async Task Gateway_ReturnsImmediately_WhileHandlerBlocks() {
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		DiscordGateway.SubscribeMessage((message, ct) => {
			started.TrySetResult();
			return gate.Task;
		});

		await DiscordGateway.DispatchMessageForTest(null);
		Assert.Equal(1, DiscordGateway.Dispatcher!.ActiveCount);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		await DiscordGateway.DispatchMessageForTest(null);
		await Task.Delay(100);
		Assert.Equal(2, DiscordGateway.Dispatcher!.ActiveCount);

		gate.SetResult();
		await DiscordGateway.DrainWorkAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(0, DiscordGateway.Dispatcher!.ActiveCount);
	}

	[Fact]
	public void Gateway_PausedRejection_NotReadyText_NoReloadWording() {
		var interaction = new FakeInteraction("fakeping");
		DiscordGateway.DispatchInteractionRejectionForTest(WorkRejectReason.Paused, interaction);
		Assert.Contains(interaction.Responses, static response => response.Contains(L.Get("RejectedPaused")));
		Assert.DoesNotContain(interaction.Responses, static response => response.Contains(L.Get("ReloadRemoved")));
	}

	[Fact]
	public async Task Gateway_NotAttached_MessageDroppedWithoutRejection() {
		DiscordGateway.DetachForTest();
		DiscordGateway.SubscribeMessage((message, ct) => Task.CompletedTask);
		var before = DiscordGateway.RejectedMessageCountForTest;
		await DiscordGateway.DispatchMessageForTest(null);
		Assert.Equal(before, DiscordGateway.RejectedMessageCountForTest);
	}

	[Fact]
	public async Task Gateway_ExitClose_Terminal_RejectionWithExitingText() {
		DiscordGateway.RequestExitClose();
		var handle = DiscordGateway.SubscribeMessage((message, ct) => Task.CompletedTask);
		try {
			var before = DiscordGateway.RejectedMessageCountForTest;
			await DiscordGateway.DispatchMessageForTest(null);
			Assert.Equal(before + 1, DiscordGateway.RejectedMessageCountForTest);
		} finally {
			handle.Dispose();
		}

		var interaction = new FakeInteraction("fakeping");
		DiscordGateway.DispatchInteractionRejectionForTest(WorkRejectReason.Exiting, interaction);
		await Task.Delay(200);
		Assert.Contains(interaction.Responses, static response => response.Contains(L.Get("RejectedExiting")));
	}

	[Fact]
	public async Task Gateway_OwnerLifecycleCancel_ReachesHandlerToken() {
		var run = MakeRun("gw-owner");
		var tokenObserved = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var handle = DiscordGateway.SubscribeMessage(run, async (message, ct) => {
			tokenObserved.SetResult(ct);
			await gate.Task;
		});

		await DiscordGateway.DispatchMessageForTest(null);
		var token = await tokenObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(token.IsCancellationRequested);
		run.Lifecycle.Cancel();
		Assert.True(token.IsCancellationRequested);
		gate.SetResult();
		handle.Dispose();
		await DiscordGateway.DrainWorkAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task RemoveModuleHandlers_DropsOwnerSubscriptions() {
		var run = MakeRun("gw-remove");
		var calls = 0;
		var otherCalls = 0;
		DiscordGateway.SubscribeMessage(run, (message, ct) => {
			Interlocked.Increment(ref calls);
			return Task.CompletedTask;
		});
		using var other = DiscordGateway.SubscribeMessage((message, ct) => {
			Interlocked.Increment(ref otherCalls);
			return Task.CompletedTask;
		});
		await DiscordGateway.DispatchMessageForTest(null);
		await DiscordGateway.DrainWorkAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(1, calls);
		DiscordGateway.RemoveModuleHandlers(run);

		await DiscordGateway.DispatchMessageForTest(null);
		await DiscordGateway.DrainWorkAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(1, calls);
		Assert.Equal(2, otherCalls);
	}

	[Fact]
	public async Task SubscriptionHandle_DisposeRemovesHandler() {
		var calls = 0;
		var handle = DiscordGateway.SubscribeMessage((message, ct) => {
			Interlocked.Increment(ref calls);
			return Task.CompletedTask;
		});
		await DiscordGateway.DispatchMessageForTest(null);
		await DiscordGateway.DrainWorkAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(1, calls);
		handle.Dispose();
		handle.Dispose();

		await DiscordGateway.DispatchMessageForTest(null);
		await DiscordGateway.DrainWorkAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task A01_MessageEvent_StartingZero_ReadyOne() {
		var initGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var run = await BootFakeModuleAsync(m => {
			if (m.Name == "FakeModule") {
				ModuleProbe.SetRuntimeStatic(m, "EnableBackgroundWork", true);
				ModuleProbe.SetRuntimeStatic(m, "InitEntered", subscribed);
				ModuleProbe.SetRuntimeStatic(m, "InitGate", initGate);
			}
		});
		try {
			await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(ModuleState.Starting, run.State);
			await DispatchAndCountAsync(run, 0);

			initGate.SetResult();
			await ModuleHostTests.WaitStateAsync(run, ModuleState.Ready);
			await DispatchAndCountAsync(run, 1);
		} finally {
			initGate.TrySetResult();
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task A01_MessageEvent_Disabled_ZeroExecution() {
		var run = await BootFakeModuleAsync(m => {
			if (m.Name == "FakeModule") {
				ModuleProbe.SetRuntimeStatic(m, "EnableBackgroundWork", true);
				ModuleProbe.SetRuntimeStatic(m, "OnInitFailure", (Func<Exception>)(() => new InvalidOperationException("配置损坏")));
			}
		});
		try {
			await ModuleHostTests.WaitStateAsync(run, ModuleState.Disabled);
			await DispatchAndCountAsync(run, 0);
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task A01_MessageEvent_StoppingAndStopped_ZeroExecution() {
		var run = await BootFakeModuleAsync(m => {
			if (m.Name == "FakeModule") {
				ModuleProbe.SetRuntimeStatic(m, "EnableBackgroundWork", true);
			}
		});
		try {
			await ModuleHostTests.WaitStateAsync(run, ModuleState.Ready);
			var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			ModuleProbe.SetRuntimeStatic(run, "StopGate", stopGate);
			var pending = ModuleHost.StopAsync(run);
			await ModuleHostTests.WaitStateAsync(run, ModuleState.Stopping);
			await DispatchAndCountAsync(run, 0);

			stopGate.SetResult();
			await pending;
			Assert.NotNull(run.FinalStopResult);
			Assert.Equal(ModuleState.Stopped, run.State);
			await DispatchAndCountAsync(run, 0);
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task A04_StopFailedResidualEntry_ClickZeroBusiness_NoDuplicateInitErrorLog() {
		var logPath = Path.Combine(Path.GetTempPath(), $"pengin1011_a04_{Guid.NewGuid():N}.log");
		Logger.SetLogPathForTest(logPath);
		Components.Clear();
		Components.SetTimeProviderForTest(null);
		try {
			var run = await BootFakeModuleAsync(m => {
				if (m.Name == "FakeModule") {
					ModuleProbe.SetRuntimeStatic(m, "OnInitFailure", (Func<Exception>)(() => new InvalidOperationException("配置损坏")));
					ModuleProbe.SetRuntimeStatic(m, "OnStopFailure", (Func<Exception>)(() => new InvalidOperationException("清理失败")));
				}
			});
			await ModuleHostTests.WaitStateAsync(run, ModuleState.Disabled);
			await ModuleHostTests.AwaitFinalStopAsync(run);
			Assert.Equal(ModuleStopOutcome.Failed, run.FinalStopResult!.Outcome);
			Assert.Contains(run, ModuleHost.Modules);

			var clicks = 0;
			Components.Register(run, "a04-residual", (interaction, ct) => {
				clicks++;
				return Task.CompletedTask;
			});
			for (var i = 0; i < 3; i++) {
				await Components.RouteByIdAsync("a04-residual", null);
			}
			Assert.Equal(0, clicks);

			Logger.SetLogPathForTest(null);
			var content = File.ReadAllText(logPath);
			Assert.Contains("配置损坏", content);
			Assert.Contains("清理失败", content);
			var initFailures = content.Split(Loc.Format("ModuleInitFailed", run.Name), StringSplitOptions.None).Length - 1;
			var cleanupFailures = content.Split(Loc.Format("ModuleCleanupFailed", run.Name), StringSplitOptions.None).Length - 1;
			Assert.Equal(1, initFailures);
			Assert.Equal(1, cleanupFailures);
		} finally {
			Logger.SetLogPathForTest(null);
			Components.Clear();
			Components.SetTimeProviderForTest(null);
			await TeardownAsync();
			File.Delete(logPath);
		}
	}

	private static LoadedModule MakeRun(string name) {
		var assembly = typeof(GatewayDispatchTests).Assembly;
		return new LoadedModule {
			Name = name,
			FilePath = "",
			Assembly = assembly,
			LoadedAt = DateTimeOffset.Now,
			Runtime = new FakeSecondRuntime(),
			State = ModuleState.Ready,
		};
	}
}
