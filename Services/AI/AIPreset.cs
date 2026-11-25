using System.Text.Json.Serialization;

namespace Pengin1011.Services.AI;

public enum AIPresetProvider {
	OpenAI,
	Gemini,
	Vertex,
}

public sealed class AIPreset {
	public string Name { get; init; } = "";
	public AIPresetProvider Provider { get; init; } = AIPresetProvider.OpenAI;
	public string ApiKey { get; init; } = "";
	public string BaseUrl { get; init; } = "";
	public string Project { get; init; } = "";
	public string Location { get; init; } = "";
	public string Model { get; init; } = "";
	public float? Temperature { get; init; }
	public float? TopP { get; init; }
	public int? MaxOutputTokens { get; init; }

	[JsonIgnore]
	public AIConnectionKey Connection => new(this);
}
