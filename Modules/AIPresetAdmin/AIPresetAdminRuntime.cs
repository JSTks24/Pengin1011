using Pengin1011.Core.Modules;

namespace Pengin1011.Modules.AIPresetAdmin;

public sealed class AIPresetAdminRuntime : IModuleRuntime {
	public Task InitializeAsync(CancellationToken ct) {
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken ct) {
		return Task.CompletedTask;
	}
}
