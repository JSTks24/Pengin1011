using QingQiu1011.Core;
using QingQiu1011.Core.Modules;

using QingQiu1011.Modules.FakeSecond;

namespace QingQiu1011.Tests;

public sealed class WorkTrackerTests : IDisposable {
	private readonly WorkTracker _tracker = new(4);

	public WorkTrackerTests() {
		_tracker.Open();
	}

	public void Dispose() {
		_tracker.Dispose();
	}

	[Fact]
	public async Task TryBegin_RegistersAndRunsWork() {
		var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var item = _tracker.TryBegin([new TrackedHandler(null, ct => { completed.SetResult(); return Task.CompletedTask; })], out var reason);

		Assert.NotNull(item);
		Assert.Equal(WorkRejectReason.Paused, reason);
		await completed.Task;
		await _tracker.DrainAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(0, _tracker.ActiveCount);
	}

	[Fact]
	public void TryBegin_BeforeOpen_RejectsPaused() {
		using var closed = new WorkTracker(4);
		var item = closed.TryBegin([new TrackedHandler(null, ct => Task.CompletedTask)], out var reason);
		Assert.Null(item);
		Assert.Equal(WorkRejectReason.Paused, reason);
	}

	[Fact]
	public void RequestExitClose_Terminal_OpenCannotRecover() {
		_tracker.Open();
		_tracker.RequestExitClose();
		Assert.True(_tracker.ExitClosedForTest);
		Assert.False(_tracker.Accepting);

		var item = _tracker.TryBegin([new TrackedHandler(null, ct => Task.CompletedTask)], out var reason);
		Assert.Null(item);
		Assert.Equal(WorkRejectReason.Exiting, reason);

		_tracker.Open();
		Assert.True(_tracker.ExitClosedForTest);
		Assert.False(_tracker.Accepting);
		var again = _tracker.TryBegin([new TrackedHandler(null, ct => Task.CompletedTask)], out var reasonAfterReopen);
		Assert.Null(again);
		Assert.Equal(WorkRejectReason.Exiting, reasonAfterReopen);
	}

	[Fact]
	public void TryBegin_AtCapacity_RejectsFull() {
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		for (var i = 0; i < 4; i++) {
			var item = _tracker.TryBegin([new TrackedHandler(null, ct => gate.Task)], out _);
			Assert.NotNull(item);
		}
		var rejected = _tracker.TryBegin([new TrackedHandler(null, ct => Task.CompletedTask)], out var reason);
		Assert.Null(rejected);
		Assert.Equal(WorkRejectReason.Full, reason);
		gate.SetResult();
	}

	[Fact]
	public async Task HandlerException_DoesNotPropagate_WorkStillCompletes() {
		var item = _tracker.TryBegin([new TrackedHandler(null, ct => throw new InvalidOperationException("业务异常"))], out _);
		Assert.NotNull(item);
		await _tracker.DrainAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(0, _tracker.ActiveCount);
	}

	[Fact]
	public async Task CancelActive_SetsItemToken() {
		var tokenObserved = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		var item = _tracker.TryBegin([new TrackedHandler(null, ct => {
			tokenObserved.SetResult(ct);
			return Task.CompletedTask;
		})], out _);
		Assert.NotNull(item);
		var token = await tokenObserved.Task;
		Assert.False(token.IsCancellationRequested);
		_tracker.CancelActive();
		await _tracker.DrainAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task OwnerLifecycleCancel_CancelsHandlerToken() {
		var run = MakeRun("owner-test");
		var tokenObserved = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var item = _tracker.TryBegin([new TrackedHandler(run, async ct => {
			tokenObserved.SetResult(ct);
			await gate.Task;
		})], out _);
		Assert.NotNull(item);
		var token = await tokenObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(token.IsCancellationRequested);
		run.Lifecycle.Cancel();
		Assert.True(token.IsCancellationRequested);
		gate.SetResult();
		await _tracker.DrainAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task Batch_HandlersRunInSubscriptionOrder() {
		var order = new List<int>();
		var item = _tracker.TryBegin([
			new TrackedHandler(null, async ct => {
				await Task.Yield();
				lock (order) order.Add(1);
			}),
			new TrackedHandler(null, ct => {
				lock (order) order.Add(2);
				return Task.CompletedTask;
			}),
		], out _);
		Assert.NotNull(item);
		await _tracker.DrainAsync(TimeSpan.FromSeconds(5));
		Assert.Equal([1, 2], order);
	}

	[Fact]
	public async Task CancelledHandler_TreatedAsCancellationNotError() {
		var observed = false;
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var item = _tracker.TryBegin([new TrackedHandler(null, async ct => {
			try {
				await gate.Task.WaitAsync(ct);
			} catch (OperationCanceledException) {
				observed = true;
				throw;
			}
		})], out _);
		Assert.NotNull(item);
		_tracker.CancelActive();
		await _tracker.DrainAsync(TimeSpan.FromSeconds(5));
		Assert.True(observed);
	}

	private static LoadedModule MakeRun(string name) {
		var assembly = typeof(WorkTrackerTests).Assembly;
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
