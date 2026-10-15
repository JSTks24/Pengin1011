using System.Text.Json;
using Pengin1011.Core;

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

	public AIConnectionKey Connection => new(this);
}

public sealed class AIPresetDocument {
	public int MaxParallel { get; init; } = 5;
	public IReadOnlyList<AIPreset> Presets { get; init; } = [];

	public AIPresetData ToData() {
		return new AIPresetData { MaxParallel = MaxParallel, Presets = [.. Presets] };
	}
}

internal sealed class AIPresetFile {
	public JsonElement MaxParallel { get; set; }
	public JsonElement Presets { get; set; }

	public bool TryToDocument(out AIPresetDocument document, out IReadOnlyList<string> errors) {
		var problems = new List<string>();
		document = new AIPresetDocument();
		var maxParallel = ReadMaxParallel(problems);
		var presets = ReadPresets(problems);
		if (problems.Count > 0) {
			errors = problems;
			return false;
		}
		document = new AIPresetDocument { MaxParallel = maxParallel, Presets = presets };
		errors = problems;
		return true;
	}

	private int ReadMaxParallel(List<string> problems) {
		if (MaxParallel.ValueKind == JsonValueKind.Undefined) return 5;
		if (MaxParallel.ValueKind == JsonValueKind.Null) {
			problems.Add(Loc.Get("AIMaxParallelNull"));
			return 5;
		}
		if (MaxParallel.ValueKind != JsonValueKind.Number || !MaxParallel.TryGetInt32(out var value)) {
			problems.Add(Loc.Format("AIMaxParallelInvalid", MaxParallel.GetRawText()));
			return 5;
		}
		if (value <= 0) problems.Add(Loc.Format("AIMaxParallelInvalid", value));
		return value;
	}

	private IReadOnlyList<AIPreset> ReadPresets(List<string> problems) {
		if (Presets.ValueKind == JsonValueKind.Undefined) return [];
		if (Presets.ValueKind == JsonValueKind.Null) {
			problems.Add(Loc.Get("AIPresetsListNull"));
			return [];
		}
		if (Presets.ValueKind != JsonValueKind.Array) {
			problems.Add(Loc.Get("AIPresetsListInvalid"));
			return [];
		}
		var list = new List<AIPreset>();
		var index = 0;
		foreach (var item in Presets.EnumerateArray()) {
			var preset = ReadPreset(item, index, problems);
			if (preset != null) list.Add(preset);
			index++;
		}
		return list;
	}

	private static AIPreset? ReadPreset(JsonElement item, int index, List<string> problems) {
		if (item.ValueKind != JsonValueKind.Object) {
			problems.Add(Loc.Format("AIPresetEntryInvalid", index));
			return null;
		}
		var name = ReadText(item, "Name", index, problems, required: true);
		var apiKey = ReadText(item, "ApiKey", index, problems, required: false);
		var baseUrl = ReadText(item, "BaseUrl", index, problems, required: false);
		var project = ReadText(item, "Project", index, problems, required: false);
		var location = ReadText(item, "Location", index, problems, required: false);
		var model = ReadText(item, "Model", index, problems, required: false);
		var provider = ReadProvider(item, index, problems);
		var temperature = ReadFloat(item, "Temperature", index, problems);
		var topP = ReadFloat(item, "TopP", index, problems);
		var maxOutputTokens = ReadInt(item, "MaxOutputTokens", index, problems);
		if (name == null || provider == null) return null;
		return new AIPreset {
			Name = name,
			Provider = provider.Value,
			ApiKey = apiKey ?? "",
			BaseUrl = baseUrl ?? "",
			Project = project ?? "",
			Location = location ?? "",
			Model = model ?? "",
			Temperature = temperature,
			TopP = topP,
			MaxOutputTokens = maxOutputTokens,
		};
	}

	private static string? ReadText(JsonElement item, string property, int index, List<string> problems, bool required) {
		if (!item.TryGetProperty(property, out var element)) return null;
		if (element.ValueKind == JsonValueKind.Null) {
			if (required) problems.Add(Loc.Format("AIPresetFieldNull", index, property));
			return null;
		}
		if (element.ValueKind != JsonValueKind.String) {
			problems.Add(Loc.Format("AIPresetFieldTypeInvalid", index, property));
			return null;
		}
		return element.GetString() ?? "";
	}

	private static AIPresetProvider? ReadProvider(JsonElement item, int index, List<string> problems) {
		if (!item.TryGetProperty("Provider", out var element)) return AIPresetProvider.OpenAI;
		if (element.ValueKind == JsonValueKind.Null) {
			problems.Add(Loc.Format("AIPresetFieldNull", index, "Provider"));
			return null;
		}
		if (element.ValueKind != JsonValueKind.String) {
			problems.Add(Loc.Format("AIPresetFieldTypeInvalid", index, "Provider"));
			return null;
		}
		var text = element.GetString() ?? "";
		if (!Enum.TryParse<AIPresetProvider>(text, ignoreCase: true, out var provider) || !Enum.IsDefined(provider)) {
			problems.Add(Loc.Format("AIPresetProviderInvalid", text));
			return null;
		}
		return provider;
	}

	private static float? ReadFloat(JsonElement item, string property, int index, List<string> problems) {
		if (!item.TryGetProperty(property, out var element)) return null;
		if (element.ValueKind == JsonValueKind.Null) return null;
		if (element.ValueKind != JsonValueKind.Number) {
			problems.Add(Loc.Format("AIPresetFieldTypeInvalid", index, property));
			return null;
		}
		var value = element.GetDouble();
		if (double.IsNaN(value) || double.IsInfinity(value)) {
			problems.Add(Loc.Format("AIPresetFieldNotFinite", index, property));
			return null;
		}
		if (value < float.MinValue || value > float.MaxValue) {
			problems.Add(Loc.Format("AIPresetFieldNotFinite", index, property));
			return null;
		}
		return (float)value;
	}

	private static int? ReadInt(JsonElement item, string property, int index, List<string> problems) {
		if (!item.TryGetProperty(property, out var element)) return null;
		if (element.ValueKind == JsonValueKind.Null) return null;
		if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value)) {
			problems.Add(Loc.Format("AIPresetFieldTypeInvalid", index, property));
			return null;
		}
		return value;
	}
}
