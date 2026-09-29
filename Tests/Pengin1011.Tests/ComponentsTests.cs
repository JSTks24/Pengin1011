using System.Runtime.Loader;
using Discord;
using Pengin1011.Core;
using Pengin1011.Core.Modules;
using Pengin1011.Modules.FakeModule;
using Pengin1011.Modules.FakeSecond;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ComponentsTests : IDisposable {
	private readonly FakeTimeProvider _time = new();

	public ComponentsTests() {
		Components.Clear();
		Components.SetTimeProviderForTest(_time);
	}

	public void Dispose() {
		Components.Clear();
		Components.SetTimeProviderForTest(null);
	}

	[Fact]
	public async Task RouteByIdAsync_RegisteredId_InvokesHandler() {
		var invoked = false;
		Components.Register("btn", (interaction, ct) => {
			invoked = true;
			return Task.CompletedTask;
		});

		await Components.RouteByIdAsync("btn", null);

		Assert.True(invoked);
	}

	[Fact]
	public async Task RouteByIdAsync_UnknownId_DoesNothing() {
		var invoked = false;
		Components.Register("btn", (interaction, ct) => {
			invoked = true;
			return Task.CompletedTask;
		});

		await Components.RouteByIdAsync("other", null);

		Assert.False(invoked);
	}

	[Fact]
	public async Task RouteByIdAsync_NullId_DoesNothing() {
		await Components.RouteByIdAsync(null, null);
	}

	[Fact]
	public async Task RouteByIdAsync_HandlerThrows_PropagatesToBoundary() {
		Components.Register("btn", (interaction, ct) => throw new InvalidOperationException("业务异常"));

		await Assert.ThrowsAsync<InvalidOperationException>(() => Components.RouteByIdAsync("btn", null));
	}

	[Fact]
	public async Task ExpiredEntry_RemovedBySweep_HandlerNotInvoked() {
		var invoked = false;
		Components.Register("btn", (interaction, ct) => {
			invoked = true;
			return Task.CompletedTask;
		}, TimeSpan.FromSeconds(10));

		_time.Advance(TimeSpan.FromSeconds(11));
		var removed = Components.SweepExpired();

		Assert.Equal(["btn"], removed);
		Assert.False(Components.TryGetState<object>("btn", out _));

		await Components.RouteByIdAsync("btn", null);
		Assert.False(invoked);
	}

	[Fact]
	public async Task TtlActive_InvokesHandler() {
		var invoked = false;
		Components.Register("btn", (interaction, ct) => {
			invoked = true;
			return Task.CompletedTask;
		}, TimeSpan.FromSeconds(60));

		_time.Advance(TimeSpan.FromSeconds(1));
		await Components.RouteByIdAsync("btn", null);

		Assert.True(invoked);
	}

	[Fact]
	public async Task ZeroTtl_NeverExpires_ButClearedWithModule() {
		var invoked = false;
		var run = MakeRun("components-zero-ttl");
		Components.Register(run, "btn", (interaction, ct) => {
			invoked = true;
			return Task.CompletedTask;
		}, TimeSpan.Zero);

		_time.Advance(TimeSpan.FromHours(1));
		Components.SweepExpired();
		await Components.RouteByIdAsync("btn", null);
		Assert.True(invoked);

		Components.RemoveModule(run);
		Assert.False(Components.Unregister("btn"));
	}

	[Fact]
	public async Task Route_RenewsExpiry_WithinSameRegistryLock() {
		var count = 0;
		Components.Register("btn", (interaction, ct) => {
			count++;
			return Task.CompletedTask;
		}, TimeSpan.FromSeconds(5));

		await Components.RouteByIdAsync("btn", null);
		_time.Advance(TimeSpan.FromSeconds(4));
		Components.SweepExpired();
		await Components.RouteByIdAsync("btn", null);
		_time.Advance(TimeSpan.FromSeconds(4));
		Components.SweepExpired();
		await Components.RouteByIdAsync("btn", null);

		Assert.Equal(3, count);

		_time.Advance(TimeSpan.FromSeconds(6));
		Components.SweepExpired();
		await Components.RouteByIdAsync("btn", null);
		Assert.Equal(3, count);
	}

	[Fact]
	public async Task Reregister_SameCustomId_NotRemovedBySweepOfOldEntry() {
		var invoked = false;
		Components.Register("btn", (interaction, ct) => Task.CompletedTask, TimeSpan.FromSeconds(1));
		_time.Advance(TimeSpan.FromSeconds(2));

		Components.Register("btn", (interaction, ct) => {
			invoked = true;
			return Task.CompletedTask;
		}, TimeSpan.FromSeconds(60));

		var removed = Components.SweepExpired();
		Assert.Empty(removed);

		await Components.RouteByIdAsync("btn", null);
		Assert.True(invoked);
	}

	[Fact]
	public void RegisterState_StateRetrievable() {
		Components.Register("panel", 42, TimeSpan.FromSeconds(60), (state, interaction, ct) => Task.CompletedTask);

		var ok = Components.TryGetState<int>("panel", out var state);

		Assert.True(ok);
		Assert.Equal(42, state);
	}

	[Fact]
	public void RegisterState_WrongType_ReturnsFalse() {
		Components.Register("panel", 42, TimeSpan.FromSeconds(60), (state, interaction, ct) => Task.CompletedTask);

		var ok = Components.TryGetState<string>("panel", out _);

		Assert.False(ok);
	}

	[Fact]
	public void TryGetState_UnknownId_ReturnsFalse() {
		var ok = Components.TryGetState<int>("missing", out _);

		Assert.False(ok);
	}

	[Fact]
	public void TryGetState_Expired_RemovesEntry() {
		Components.Register("panel", 42, TimeSpan.FromSeconds(1), (state, interaction, ct) => Task.CompletedTask);

		_time.Advance(TimeSpan.FromSeconds(2));
		var ok = Components.TryGetState<int>("panel", out _);

		Assert.False(ok);
	}

	[Fact]
	public void ExpiredState_IsCollectableAfterSweep() {
		var weak = RegisterAndExpireState();

		Components.SweepExpired();
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		Assert.False(weak.IsAlive);
	}

	private static WeakReference RegisterAndExpireState() {
		var state = new ExpensiveState();
		Components.SetTimeProviderForTest(null);
		Components.Register("collect-me", state, TimeSpan.FromMilliseconds(1), (s, interaction, ct) => Task.CompletedTask);
		Thread.Sleep(20);
		return new WeakReference(state);
	}

	private sealed class ExpensiveState {
		public byte[] Payload { get; } = new byte[1024];
	}

	[Fact]
	public async Task Unregister_RemovesEntry() {
		var invoked = false;
		Components.Register("btn", (interaction, ct) => {
			invoked = true;
			return Task.CompletedTask;
		});

		var removed = Components.Unregister("btn");
		await Components.RouteByIdAsync("btn", null);

		Assert.True(removed);
		Assert.False(invoked);
		Assert.False(Components.Unregister("btn"));
	}

	[Fact]
	public void Register_EmptyCustomId_Throws() {
		Assert.Throws<ArgumentException>(() => Components.Register("", (interaction, ct) => Task.CompletedTask));
	}

	[Fact]
	public void Register_NullHandler_Throws() {
		Assert.Throws<ArgumentNullException>(() => Components.Register("btn", null!));
	}

	[Fact]
	public void RemoveModule_OnlyDropsOwnedEntries() {
		var run = MakeRun("components-owner");
		var other = MakeRun("components-other");
		Components.Register(run, "mine", 1, null, (state, interaction, ct) => Task.CompletedTask);
		Components.Register(other, "theirs", 2, null, (state, interaction, ct) => Task.CompletedTask);

		Components.RemoveModule(run);

		Assert.False(Components.TryGetState<int>("mine", out _));
		Assert.True(Components.TryGetState<int>("theirs", out _));
	}

	[Fact]
	public async Task Attach_CreatesSingleSweeper_ShutdownStopsIt() {
		Components.Detach();
		Components.Attach();
		Components.Attach();
		Assert.True(Components.SweeperRunningForTest);

		await Components.ShutdownAsync();
		Assert.False(Components.SweeperRunningForTest);

		Components.Attach();
		Assert.True(Components.SweeperRunningForTest);
		await Components.ShutdownAsync();
		Components.Detach();
	}

	private static LoadedModule MakeRun(string name) {
		var assembly = typeof(ComponentsTests).Assembly;
		return new LoadedModule {
			Name = name,
			FilePath = "",
			Assembly = assembly,
			LoadedAt = DateTimeOffset.Now,
			Runtime = new FakeSecondRuntime(),
			State = ModuleState.Ready,
		};
	}

	[Fact]
	public async Task A01_RouteByIdAsync_StateMatrix_OnlyReadyExecutes() {
		var invoked = 0;
		var run = MakeRun("a01-components");
		Components.Register(run, "a01-btn", (interaction, ct) => {
			invoked++;
			return Task.CompletedTask;
		});

		foreach (var state in new[] { ModuleState.Starting, ModuleState.Disabled, ModuleState.Stopping, ModuleState.Stopped }) {
			run.State = state;
			await Components.RouteByIdAsync("a01-btn", null);
			Assert.Equal(0, invoked);
		}

		run.State = ModuleState.Ready;
		await Components.RouteByIdAsync("a01-btn", null);
		Assert.Equal(1, invoked);
	}

	[Fact]
	public async Task A02_RouteByIdAsync_DisabledOwner_ZeroExecution_NoTtlRenewal() {
		var invoked = false;
		var run = MakeRun("a02-components");
		run.State = ModuleState.Disabled;
		Components.Register(run, "a02-btn", (interaction, ct) => {
			invoked = true;
			return Task.CompletedTask;
		}, TimeSpan.FromSeconds(10));

		_time.Advance(TimeSpan.FromSeconds(5));
		await Components.RouteByIdAsync("a02-btn", null);
		Assert.False(invoked);

		_time.Advance(TimeSpan.FromSeconds(6));
		Assert.Equal(["a02-btn"], Components.SweepExpired());
	}

	[Fact]
	public async Task A02_RespondUnavailable_EphemeralText_HasRespondedGuard() {
		var fresh = new FakeInteraction("fakeping");
		await Components.RespondUnavailableAsync(fresh);
		Assert.Contains(fresh.Responses, static response => response.Contains(L.Get("ModuleUnavailable")));

		var responded = new FakeInteraction("fakeping") { HasResponded = true };
		await Components.RespondUnavailableAsync(responded);
		Assert.Empty(responded.Responses);
	}

	[Fact]
	public async Task A03_RouteByIdAsync_OwnerLifecycleCancel_ReachesHandlerToken() {
		var run = MakeRun("a03-components");
		var tokenObserved = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Components.Register(run, "a03-btn", async (interaction, ct) => {
			tokenObserved.SetResult(ct);
			await gate.Task.WaitAsync(ct);
		});

		var route = Components.RouteByIdAsync("a03-btn", null);
		var token = await tokenObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(token.IsCancellationRequested);

		run.Lifecycle.Cancel();
		Assert.True(token.IsCancellationRequested);
		gate.SetResult();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => route);
	}

	[Fact]
	public async Task A03_RouteByIdAsync_RequestTokenCancel_DoesNotCancelModule() {
		var run = MakeRun("a03-single-request");
		var tokenObserved = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Components.Register(run, "a03-req-btn", async (interaction, ct) => {
			tokenObserved.SetResult(ct);
			await gate.Task.WaitAsync(ct);
		});

		using var requestCts = new CancellationTokenSource();
		var route = Components.RouteByIdAsync("a03-req-btn", null, requestCts.Token);
		var token = await tokenObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(token.IsCancellationRequested);

		requestCts.Cancel();
		Assert.True(token.IsCancellationRequested);
		Assert.Equal(ModuleState.Ready, run.State);
		Assert.False(run.Lifecycle.IsCancellationRequested);

		gate.SetResult();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => route);
	}

	[Fact]
	public async Task A05_RemoveModule_OldVersion_SameCustomId_NewVersionSurvives() {
		var oldInvoked = false;
		var newInvoked = false;
		var oldRun = MakeRun("a05-old");
		var newRun = MakeRun("a05-new");
		Components.Register(oldRun, "a05-btn", (interaction, ct) => {
			oldInvoked = true;
			return Task.CompletedTask;
		});
		Components.Register(newRun, "a05-btn", (interaction, ct) => {
			newInvoked = true;
			return Task.CompletedTask;
		});

		Components.RemoveModule(oldRun);

		await Components.RouteByIdAsync("a05-btn", null);
		Assert.False(oldInvoked);
		Assert.True(newInvoked);
	}

	[Fact]
	public async Task A06_RouteByIdAsync_NullOwner_WorksIndependentOfModuleLifecycle() {
		var invoked = false;
		Components.Register("a06-host-btn", (interaction, ct) => {
			invoked = true;
			return Task.CompletedTask;
		});

		var run = MakeRun("a06-bystander");
		run.Lifecycle.Cancel();
		run.State = ModuleState.Stopped;

		await Components.RouteByIdAsync("a06-host-btn", null);

		Assert.True(invoked);
	}
}
public sealed class FakeTimeProvider : TimeProvider {
	private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	public override DateTimeOffset GetUtcNow() => _now;

	public void Advance(TimeSpan delta) {
		_now += delta;
	}
}
