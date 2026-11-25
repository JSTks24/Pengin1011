using Discord.Interactions;
using Pengin1011.Core.Modules;

namespace Pengin1011.Modules.FakeModule;

[ModuleAvailability(typeof(FakeModuleRuntime))]
public class FakeModule : InteractionModuleBase<FakeInteractionContext> {
	public static int CommandCount;
	public static Exception? CommandFailure;
	public static TaskCompletionSource? CommandGate;

	public static void ResetCommands() {
		CommandCount = 0;
		CommandFailure = null;
		CommandGate = null;
	}

	[SlashCommand("fakeping", "测试命令")]
	public async Task FakePing() {
		CommandCount++;
		if (CommandFailure != null) throw CommandFailure;
		if (CommandGate != null) await CommandGate.Task;
	}
}
