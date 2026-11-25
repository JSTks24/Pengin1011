namespace Pengin1011.Services.AI;

internal sealed class AIPresetPatchBuilder {
	private readonly System.Globalization.CultureInfo _number = System.Globalization.CultureInfo.InvariantCulture;
	private Patch<string> _name;
	private Patch<AIPresetProvider> _provider;
	private Patch<string> _apiKey;
	private Patch<string> _baseUrl;
	private Patch<string> _project;
	private Patch<string> _location;
	private Patch<string> _model;
	private Patch<float> _temperature;
	private Patch<float> _topP;
	private Patch<int> _maxOutputTokens;

	public bool Apply(string field, string value, out PresetEditFailure failure) {
		failure = default;
		switch (field) {
			case "name":
				if (value.Trim().Length == 0) {
					failure = new PresetEditFailure(PresetEditFailureKind.NameRequired, value);
					return false;
				}
				_name = Patch<string>.Of(value);
				return true;
			case "provider":
				if (!PresetEdit.TryProvider(value, out var provider)) {
					failure = new PresetEditFailure(PresetEditFailureKind.ProviderInvalid, value);
					return false;
				}
				_provider = Patch<AIPresetProvider>.SetTo(provider);
				return true;
			case "apikey":
				_apiKey = Patch<string>.Of(value);
				return true;
			case "baseurl":
				_baseUrl = Patch<string>.Of(value);
				return true;
			case "project":
				_project = Patch<string>.Of(value);
				return true;
			case "location":
				_location = Patch<string>.Of(value);
				return true;
			case "model":
				_model = Patch<string>.Of(value);
				return true;
			case "temperature":
				if (!TryFloat(value, out var temperature)) {
					failure = new PresetEditFailure(PresetEditFailureKind.ValueInvalid, value);
					return false;
				}
				_temperature = temperature;
				return true;
			case "topp":
				if (!TryFloat(value, out var topP)) {
					failure = new PresetEditFailure(PresetEditFailureKind.ValueInvalid, value);
					return false;
				}
				_topP = topP;
				return true;
			case "maxoutputtokens":
				if (!TryInt(value, out var maxOutputTokens)) {
					failure = new PresetEditFailure(PresetEditFailureKind.ValueInvalid, value);
					return false;
				}
				_maxOutputTokens = maxOutputTokens;
				return true;
			default:
				failure = new PresetEditFailure(PresetEditFailureKind.FieldUnknown, field);
				return false;
		}
	}

	public AIPresetPatch Build() {
		return new AIPresetPatch {
			Name = _name,
			Provider = _provider,
			ApiKey = _apiKey,
			BaseUrl = _baseUrl,
			Project = _project,
			Location = _location,
			Model = _model,
			Temperature = _temperature,
			TopP = _topP,
			MaxOutputTokens = _maxOutputTokens,
		};
	}

	private bool TryFloat(string value, out Patch<float> patch) {
		if (value.Length == 0) {
			patch = Patch<float>.Clear;
			return true;
		}
		if (float.TryParse(value, System.Globalization.NumberStyles.Float, _number, out var parsed)) {
			patch = Patch<float>.SetTo(parsed);
			return true;
		}
		patch = Patch<float>.Absent;
		return false;
	}

	private bool TryInt(string value, out Patch<int> patch) {
		if (value.Length == 0) {
			patch = Patch<int>.Clear;
			return true;
		}
		if (int.TryParse(value, System.Globalization.NumberStyles.Integer, _number, out var parsed)) {
			patch = Patch<int>.SetTo(parsed);
			return true;
		}
		patch = Patch<int>.Absent;
		return false;
	}
}
