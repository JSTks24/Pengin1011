using Discord.WebSocket;
using Pengin1011.Core;
using Pengin1011.Core.Modules;
using Pengin1011.Services.Discord;

namespace Pengin1011.Modules.FakeModule;

public sealed class FakeModuleRuntime : IModuleRuntime {
	public static int InitCount;
	public static int StopCount;
	public static int BackgroundEventCount;
	public static bool EnableBackgroundWork;
	public static bool InitIgnoreCancel;
	public static bool StopEntryTokenCancelled;
	public static Func<Exception>? OnInitFailure;
	public static Func<Exception>? OnStopFailure;
	public static TaskCompletionSource? InitGate;
	public static TaskCompletionSource? StopGate;
	public static TaskCompletionSource? StopSyncGate;
	public static TaskCompletionSource? InitEntered;
	public static TaskCompletionSource? StopEntered;
	public static List<string> Events = [];
	public static List<object> CreatedResources { get; } = [];

	private IDisposable? _backgroundSubscription;

	public static void Reset() {
		InitCount = 0;
		StopCount = 0;
		BackgroundEventCount = 0;
		EnableBackgroundWork = false;
		InitIgnoreCancel = false;
		StopEntryTokenCancelled = false;
		OnInitFailure = null;
		OnStopFailure = null;
		InitGate = null;
		StopGate = null;
		StopSyncGate = null;
		InitEntered = null;
		StopEntered = null;
		Events.Clear();
		CreatedResources.Clear();
	}

	private static TaskCompletionSource NewGate() {
		return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	[ModuleBridgeExport]
	public string DescribeState() {
		return $"FakeModuleRuntime-{InitCount}-{StopCount}";
	}

	public async Task InitializeAsync(CancellationToken ct) {
		InitCount++;
		Events.Add("init-entered");
		try {
			if (EnableBackgroundWork) {
				var run = ModuleRegistry.RunOf(GetType().Assembly);
				if (run != null) {
					CreatedResources.Add(new object());
					_backgroundSubscription = DiscordGateway.SubscribeMessage(run, OnBackgroundMessage);
				}
			}
			(InitEntered ??= NewGate()).TrySetResult();
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
			if (StopSyncGate != null) StopSyncGate.Task.Wait();
			_backgroundSubscription?.Dispose();
			_backgroundSubscription = null;
			if (StopGate != null) await StopGate.Task.WaitAsync(ct);
			if (OnStopFailure != null) throw OnStopFailure();
		} finally {
			Events.Add("stop-finished");
		}
	}

	private Task OnBackgroundMessage(SocketMessage message, CancellationToken ct) {
		BackgroundEventCount++;
		return Task.CompletedTask;
	}
}
