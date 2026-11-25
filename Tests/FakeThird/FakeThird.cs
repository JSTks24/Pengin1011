using Discord.Interactions;
using Pengin1011.Core.Modules;
using Pengin1011.Modules.FakeModule;

namespace Pengin1011.Modules.FakeThird;

[ModuleAvailability(typeof(FakeThirdRuntime))]
public class FakeThird : InteractionModuleBase<FakeInteractionContext> {
	public static int CommandCount;

	public static void Reset() {
		CommandCount = 0;
	}

	[SlashCommand("fakethird", "第三个测试模块命令")]
	public async Task FakePing() {
		CommandCount++;
	}
}
