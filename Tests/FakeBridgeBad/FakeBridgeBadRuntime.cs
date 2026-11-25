using Pengin1011.Core.Modules;

namespace Pengin1011.Modules.FakeBridgeBad;

public sealed class FakeBridgeBadRuntime : IModuleRuntime {
	public static int InitCount;
	public static int StopCount;

	public static void Reset() {
		InitCount = 0;
		StopCount = 0;
	}

	public Task InitializeAsync(CancellationToken ct) {
		InitCount++;
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken ct) {
		StopCount++;
		return Task.CompletedTask;
	}
}
