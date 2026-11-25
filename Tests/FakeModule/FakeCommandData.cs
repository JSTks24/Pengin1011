using Discord;

namespace Pengin1011.Modules.FakeModule;

public sealed class FakeCommandData : IApplicationCommandInteractionData {
	public FakeCommandData(string name) {
		Name = name;
	}

	public string Name { get; }
	public ulong Id => 1;
	public ApplicationCommandType Type => ApplicationCommandType.Slash;
	public IReadOnlyCollection<IApplicationCommandInteractionDataOption> Options => [];
}
