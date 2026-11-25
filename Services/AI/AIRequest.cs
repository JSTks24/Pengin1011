namespace Pengin1011.Services.AI;

public sealed class AIRequest {
	public string System { get; init; } = "";
	public required IReadOnlyList<AIMessage> Messages { get; init; }
	public IReadOnlyList<AIImage> Images { get; init; } = [];
	public string? Model { get; init; }
	public float? Temperature { get; init; }
	public float? TopP { get; init; }
	public int? MaxOutputTokens { get; init; }
}
