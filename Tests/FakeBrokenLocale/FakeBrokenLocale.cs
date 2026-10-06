using Pengin1011.Core.Modules;

namespace Pengin1011.Modules.FakeBrokenLocale;

public sealed class FakeBrokenLocaleRuntime : IModuleRuntime {
	public Task InitializeAsync(CancellationToken ct) {
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken ct) {
		return Task.CompletedTask;
	}
}
