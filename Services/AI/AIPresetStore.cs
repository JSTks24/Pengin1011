using Pengin1011.Core;
using Pengin1011.Core.Logging;
using Pengin1011.Helper;

namespace Pengin1011.Services.AI;

public static class AIPresetStore {
	private const string Template = """
		{
		  "MaxParallel": 5,
		  "Presets": []
		}
		""";

	private static readonly object _gate = new();

	private static AIPresetSnapshot _snapshot = AIPresetSnapshot.Empty;
	private static string _path = DefaultPath;

	public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "aipresets.json");

	public static AIPresetSnapshot Snapshot => Volatile.Read(ref _snapshot);

	public static AIPresetDocument Document {
		get {
			var snapshot = Snapshot;
			return new AIPresetDocument { MaxParallel = snapshot.MaxParallel, Presets = snapshot.Presets };
		}
	}

	public static IReadOnlyList<AIPreset> Presets => Snapshot.Presets;

	public static AIPreset? DefaultPreset => Snapshot.Default;

	public static int MaxParallel => Snapshot.MaxParallel;

	public static string CurrentPath {
		get {
			lock (_gate) return _path;
		}
	}

	public static AIPreset? Find(string? name) {
		return Snapshot.Find(name);
	}

	public static bool TryLoad(string? path, out IReadOnlyList<string> errors) {
		var list = new List<string>();
		var file = path ?? DefaultPath;
		if (!File.Exists(file)) {
			try {
				if (JsonHelper.EnsureTemplateFile(file, Template)) {
					var created = Loc.Format("AIPresetsCreatedFromTemplate", file);
					Logger.Info(typeof(AIPresetStore), created);
					list.Add(created);
				}
			} catch (Exception e) {
				list.Add(Loc.Format("AIPresetsTemplateWriteFailed", file));
				Logger.Error(typeof(AIPresetStore), e, Loc.Format("AIPresetsTemplateWriteFailed", file));
			}
			errors = list;
			return false;
		}
		AIPresetDocument document;
		try {
			var parsed = JsonHelper.Load<AIPresetFile>(file);
			if (!parsed.TryToDocument(out document, out var problems)) {
				foreach (var problem in problems) Logger.Error(typeof(AIPresetStore), problem);
				errors = problems;
				return false;
			}
		} catch (Exception e) {
			list.Add(Loc.Format("AIPresetsReadFailed", file));
			Logger.Error(typeof(AIPresetStore), e, Loc.Format("AIPresetsReadFailed", file));
			errors = list;
			return false;
		}
		lock (_gate) {
			var problems = ValidateDocumentCore(document);
			if (problems.Count > 0) {
				foreach (var problem in problems) Logger.Error(typeof(AIPresetStore), problem);
				errors = problems;
				return false;
			}
			_path = file;
			Publish(document.ToData());
		}
		AIClient.RefreshConnections();
		errors = list;
		return true;
	}

	public static bool Apply(string name, AIPresetPatch patch, long? expectedVersion, out AIPresetEditResult result) {
		lock (_gate) {
			var snapshot = Snapshot;
			var index = IndexOf(snapshot.Presets, name);
			if (index < 0) {
				result = AIPresetEditResult.Fail(Loc.Format("AIPresetNotFound", name));
				return false;
			}
			var candidate = patch.Apply(snapshot.Presets[index]);
			if (!string.Equals(candidate.Name, name, StringComparison.Ordinal)) {
				var references = AIPresetUsageRegistry.ReferencesTo(name);
				if (references.Count > 0) {
					result = AIPresetEditResult.Fail(Loc.Format("AIPresetInUse", name, string.Join(Loc.Get("ListSeparatorItems"), references)));
					return false;
				}
			}
			var list = new List<AIPreset>(snapshot.Presets) { [index] = candidate };
			if (!Commit(new AIPresetData { MaxParallel = snapshot.MaxParallel, Presets = list }, expectedVersion, out result)) return false;
		}
		AIClient.RefreshConnections();
		return true;
	}

	public static bool AddPatch(AIPreset candidate, long? expectedVersion, out AIPresetEditResult result) {
		lock (_gate) {
			var snapshot = Snapshot;
			var list = new List<AIPreset>(snapshot.Presets) { candidate };
			if (!Commit(new AIPresetData { MaxParallel = snapshot.MaxParallel, Presets = list }, expectedVersion, out result)) return false;
		}
		AIClient.RefreshConnections();
		return true;
	}

	public static bool RemovePatch(string name, long? expectedVersion, out AIPresetEditResult result) {
		lock (_gate) {
			var snapshot = Snapshot;
			var index = IndexOf(snapshot.Presets, name);
			if (index < 0) {
				result = AIPresetEditResult.Fail(Loc.Format("AIPresetNotFound", name));
				return false;
			}
			var references = AIPresetUsageRegistry.ReferencesTo(name);
			if (references.Count > 0) {
				result = AIPresetEditResult.Fail(Loc.Format("AIPresetInUse", name, string.Join(Loc.Get("ListSeparatorItems"), references)));
				return false;
			}
			var list = new List<AIPreset>(snapshot.Presets);
			list.RemoveAt(index);
			if (index == 0 && list.Count == 0 && AIPresetUsageRegistry.HasDefaultConsumers) {
				result = AIPresetEditResult.Fail(Loc.Format("AIPresetDefaultInUse", name));
				return false;
			}
			if (!Commit(new AIPresetData { MaxParallel = snapshot.MaxParallel, Presets = list }, expectedVersion, out result)) return false;
		}
		AIClient.RefreshConnections();
		return true;
	}

	public static IReadOnlyList<string> ValidateDocument(AIPresetDocument document) {
		return ValidateDocumentCore(document);
	}

	public static IReadOnlyList<string> ValidatePreset(AIPreset preset, IReadOnlyList<AIPreset> existing, int? excludeIndex) {
		var problems = new List<string>();
		ValidatePresetCore(preset, existing, excludeIndex, problems);
		return problems;
	}

	private static bool Commit(AIPresetData data, long? expectedVersion, out AIPresetEditResult result) {
		var snapshot = Snapshot;
		if (expectedVersion is long expected && expected != snapshot.Version) {
			result = AIPresetEditResult.Fail(Loc.Format("AIPresetStaleVersion", expected, snapshot.Version));
			return false;
		}
		var problems = ValidateDocumentCore(new AIPresetDocument { MaxParallel = data.MaxParallel, Presets = data.Presets });
		var consistency = ValidateDependencies(data.Presets);
		if (consistency != null) problems.Add(consistency);
		if (problems.Count > 0) {
			result = AIPresetEditResult.Fail(string.Join("; ", problems));
			return false;
		}
		if (!JsonHelper.Save(_path, new AIPresetDocument { MaxParallel = data.MaxParallel, Presets = data.Presets })) {
			result = AIPresetEditResult.Fail(Loc.Get("AIPresetsSaveFailed"));
			return false;
		}
		Publish(data);
		result = AIPresetEditResult.Ok(Snapshot.Version);
		return true;
	}

	private static void Publish(AIPresetData data) {
		Volatile.Write(ref _snapshot, new AIPresetSnapshot(Snapshot.Version + 1, data));
	}

	private static string? ValidateDependencies(IReadOnlyList<AIPreset> presets) {
		var failures = AIPresetValidator.CollectMissing(presets, AIPresetUsageRegistry.Usages);
		return failures.Count == 0 ? null : string.Join("; ", failures);
	}

	private static List<string> ValidateDocumentCore(AIPresetDocument document) {
		var problems = new List<string>();
		if (document.MaxParallel <= 0) problems.Add(Loc.Format("AIMaxParallelInvalid", document.MaxParallel));
		var presets = document.Presets;
		for (var i = 0; i < presets.Count; i++) {
			ValidatePresetCore(presets[i], presets, i, problems);
		}
		return problems;
	}

	private static void ValidatePresetCore(AIPreset preset, IReadOnlyList<AIPreset> existing, int? excludeIndex, List<string> problems) {
		var name = preset.Name;
		if (name.Trim().Length == 0) {
			problems.Add(Loc.Get("AIPresetNameRequired"));
		} else if (!string.Equals(name, name.Trim(), StringComparison.Ordinal)) {
			problems.Add(Loc.Format("AIPresetNameWhitespace", name));
		} else if (ContainsControlCharacter(name)) {
			problems.Add(Loc.Format("AIPresetNameControlCharacter", name));
		} else {
			for (var i = 0; i < existing.Count; i++) {
				if (excludeIndex == i) continue;
				if (string.Equals(existing[i].Name, name, StringComparison.Ordinal)) {
					problems.Add(Loc.Format("AIPresetDuplicateName", name));
					break;
				}
			}
		}
		switch (preset.Provider) {
			case AIPresetProvider.OpenAI:
				if (preset.ApiKey.Trim().Length == 0) problems.Add(Loc.Get("AIPresetApiKeyRequired"));
				if (preset.Model.Trim().Length == 0) problems.Add(Loc.Get("AIPresetModelRequired"));
				if (preset.BaseUrl.Trim().Length == 0) {
					problems.Add(Loc.Get("AIPresetBaseUrlRequired"));
				} else if (!Uri.TryCreate(preset.BaseUrl.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) {
					problems.Add(Loc.Format("AIPresetBaseUrlInvalid", preset.BaseUrl));
				}
				break;
			case AIPresetProvider.Gemini:
				if (preset.ApiKey.Trim().Length == 0) problems.Add(Loc.Get("AIPresetApiKeyRequired"));
				if (preset.Model.Trim().Length == 0) problems.Add(Loc.Get("AIPresetModelRequired"));
				if (!GoogleAIEndpoint.TryResolve(preset, out _)) problems.Add(Loc.Format("AIPresetBaseUrlInvalid", preset.BaseUrl));
				break;
			case AIPresetProvider.Vertex:
				if (preset.Project.Trim().Length == 0 || preset.Location.Trim().Length == 0) problems.Add(Loc.Get("AIPresetVertexFieldsRequired"));
				if (preset.Model.Trim().Length == 0) problems.Add(Loc.Get("AIPresetModelRequired"));
				if (!GoogleAIEndpoint.TryResolve(preset, out _)) problems.Add(Loc.Format("AIPresetBaseUrlInvalid", preset.BaseUrl));
				break;
			default:
				problems.Add(Loc.Format("AIPresetProviderInvalid", preset.Provider));
				break;
		}
		if (preset.Temperature is float temperature && (float.IsNaN(temperature) || float.IsInfinity(temperature) || temperature < 0)) {
			problems.Add(Loc.Format("AITemperatureInvalid", temperature));
		}
		if (preset.TopP is float topP && (float.IsNaN(topP) || float.IsInfinity(topP) || topP < 0)) {
			problems.Add(Loc.Format("AITopPInvalid", topP));
		}
		if (preset.MaxOutputTokens is <= 0) problems.Add(Loc.Format("AIMaxOutputTokensInvalid", preset.MaxOutputTokens));
	}

	private static bool ContainsControlCharacter(string value) {
		foreach (var character in value) {
			if (char.IsControl(character)) return true;
		}
		return false;
	}

	private static int IndexOf(IReadOnlyList<AIPreset> presets, string name) {
		for (var i = 0; i < presets.Count; i++) {
			if (string.Equals(presets[i].Name, name, StringComparison.Ordinal)) return i;
		}
		return -1;
	}

	internal static void LoadForTest(AIPresetDocument document, string? path = null) {
		lock (_gate) {
			_path = path ?? DefaultPath;
			Publish(document.ToData());
		}
	}

	internal static void ResetForTest() {
		lock (_gate) {
			_path = DefaultPath;
			Volatile.Write(ref _snapshot, AIPresetSnapshot.Empty);
		}
	}
}
