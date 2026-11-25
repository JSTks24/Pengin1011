namespace Pengin1011.Core;

public sealed class DiscordConfig {
	public string Token { get; init; } = "";
	public IReadOnlyList<ulong> AdminRoleIds { get; init; } = [];
}
