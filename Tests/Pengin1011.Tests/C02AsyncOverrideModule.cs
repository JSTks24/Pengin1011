using Discord.Interactions;
using Pengin1011.Core.Modules;
using Pengin1011.Modules.FakeModule;
using Pengin1011.Modules.FakeSecond;

namespace Pengin1011.Tests;

[ModuleAvailability(typeof(FakeSecondRuntime))]
public class C02AsyncOverrideModule : InteractionModuleBase<FakeInteractionContext> {
	public static int CommandCount;

	[SlashCommand("c02_async_override", "RunMode 覆盖拒绝用例", false, RunMode.Async)]
	public Task Body() {
		CommandCount++;
		return Task.CompletedTask;
	}
}
