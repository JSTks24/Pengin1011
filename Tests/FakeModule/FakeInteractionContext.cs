using Discord;

namespace Pengin1011.Modules.FakeModule;

public sealed class FakeInteractionContext : IInteractionContext {
	public FakeInteractionContext(FakeInteraction interaction) {
		Interaction = interaction;
	}

	public IDiscordClient Client => null!;
	public IGuild Guild => null!;
	public IMessageChannel Channel => null!;
	public IUser User => null!;
	public IDiscordInteraction Interaction { get; }
}
