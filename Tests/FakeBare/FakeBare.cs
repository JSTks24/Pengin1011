using Discord.Interactions;
using QingQiu1011.Core.Modules;

namespace QingQiu1011.Modules.FakeBare;

public class FakeBare : InteractionModuleBase<SocketInteractionContext> {
	[SlashCommand("fakebare", "缺少可用性特性的命令")]
	public async Task FakePing() {
		await Task.CompletedTask;
	}
}

public sealed class FakeBareRuntime : IModuleRuntime {
	public Task InitializeAsync(CancellationToken ct) {
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken ct) {
		return Task.CompletedTask;
	}
}
