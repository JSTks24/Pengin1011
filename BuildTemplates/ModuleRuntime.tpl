using Pengin1011.Core;
using Pengin1011.Core.Modules;

namespace Pengin1011.Modules.__MODULE__;

public sealed class __MODULE__Runtime : IModuleRuntime {
	public __MODULE__Options Options { get; private set; } = new();

	public async Task InitializeAsync(CancellationToken ct) {
		Options = __MODULE__Config.Load();
		await Databases.PrepareAsync<__MODULE__DbContext>(ct);
	}

	public Task StopAsync(CancellationToken ct) {
		return Task.CompletedTask;
	}
}
