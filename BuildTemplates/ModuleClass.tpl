using Discord.Interactions;
using QingQiu1011.Core.Modules;

namespace QingQiu1011.Modules.__MODULE__;

[ModuleAvailability(typeof(__MODULE__Runtime))]
public class __MODULE__ : InteractionModuleBase<SocketInteractionContext> {
	[SlashCommand("example", "示例命令，请修改命令名与实现")]
	public async Task Example() {
		await RespondAsync("ok", ephemeral: true);
	}
}
