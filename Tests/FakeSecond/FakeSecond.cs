using Pengin1011.Core.Modules;

namespace Pengin1011.Modules.FakeSecond;

public sealed class FakeSecondRuntime : IModuleRuntime {
	public static int InitCount;
	public static int StopCount;
	public static bool InitIgnoreCancel;
	public static bool StopEntryTokenCancelled;
	public static Func<Exception>? OnInitFailure;
	public static Func<Exception>? OnStopFailure;
	public static TaskCompletionSource? InitGate;
	public static TaskCompletionSource? StopGate;
	public static TaskCompletionSource? InitEntered;
	public static TaskCompletionSource? StopEntered;
	public static List<string> Events = [];

	public static void Reset() {
		InitCount = 0;
		StopCount = 0;
		InitIgnoreCancel = false;
		StopEntryTokenCancelled = false;
		OnInitFailure = null;
		OnStopFailure = null;
		InitGate = null;
		StopGate = null;
		InitEntered = null;
		StopEntered = null;
		Events.Clear();
	}

	private static TaskCompletionSource NewGate() {
		return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	public async Task InitializeAsync(CancellationToken ct) {
		InitCount++;
		Events.Add("init-entered");
		(InitEntered ??= NewGate()).TrySetResult();
		try {
			if (InitGate != null) {
				if (InitIgnoreCancel) await InitGate.Task;
				else await InitGate.Task.WaitAsync(ct);
			}
			if (OnInitFailure != null) throw OnInitFailure();
		} finally {
			Events.Add("init-finished");
		}
	}

	public async Task StopAsync(CancellationToken ct) {
		StopCount++;
		StopEntryTokenCancelled = ct.IsCancellationRequested;
		Events.Add("stop-entered");
		(StopEntered ??= NewGate()).TrySetResult();
		try {
			if (StopGate != null) await StopGate.Task.WaitAsync(ct);
			if (OnStopFailure != null) throw OnStopFailure();
		} finally {
			Events.Add("stop-finished");
		}
	}
}
