using Discord.Interactions;

namespace Pengin1011.Modules.FakeBare;

public class FakeBare : InteractionModuleBase<SocketInteractionContext> {
	[SlashCommand("fakebare", "缺少可用性特性的命令")]
	public async Task FakePing() {
		await Task.CompletedTask;
	}
}
