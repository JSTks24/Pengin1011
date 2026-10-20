using System.Text.Json;
using Pengin1011.Helper;
using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class AIPresetStoreTests : IDisposable {
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"qq1011_presets_{Guid.NewGuid():N}");
	private readonly string _path;

	public AIPresetStoreTests() {
		Directory.CreateDirectory(_directory);
		_path = Path.Combine(_directory, "aipresets.json");
		AIPresetUsageRegistry.ForgetAll();
	}

	public void Dispose() {
		AIPresetStore.ResetForTest();
		AIPresetUsageRegistry.ForgetAll();
		try { Directory.Delete(_directory, true); } catch (IOException) { }
	}

	private static AIPreset OpenAi(string name = "prod", string model = "m1") {
		return new AIPreset {
			Name = name,
			Provider = AIPresetProvider.OpenAI,
			ApiKey = "k",
			BaseUrl = "https://api.example.com/v1",
			Model = model,
		};
	}

	private static AIPreset Copy(AIPreset preset, string? baseUrl = null, float? temperature = null, float? topP = null, int? maxOutputTokens = null) {
		return new AIPreset {
			Name = preset.Name,
			Provider = preset.Provider,
			ApiKey = preset.ApiKey,
			BaseUrl = baseUrl ?? preset.BaseUrl,
			Project = preset.Project,
			Location = preset.Location,
			Model = preset.Model,
			Temperature = temperature ?? preset.Temperature,
			TopP = topP ?? preset.TopP,
			MaxOutputTokens = maxOutputTokens ?? preset.MaxOutputTokens,
		};
	}

	[Fact]
	public void TryLoad_MissingFile_WritesTemplateAndFails() {
		var missing = Path.Combine(_directory, "nested", "aipresets.json");

		Assert.False(AIPresetStore.TryLoad(missing, out var errors));

		Assert.Contains(errors, static e => e.Contains(L.Prefix("AIPresetsCreatedFromTemplate")));
		Assert.True(File.Exists(missing));
		var content = File.ReadAllText(missing);
		Assert.Contains("\"MaxParallel\"", content);
		Assert.Contains("\"Presets\"", content);
	}

	[Fact]
	public void TryLoad_ExistingFile_CreatesNothing() {
		File.WriteAllText(_path, """
			{ "MaxParallel": 3, "Presets": [] }
			""");

		Assert.True(AIPresetStore.TryLoad(_path, out var errors));

		Assert.Empty(errors);
		Assert.Equal(3, AIPresetStore.MaxParallel);
		Assert.Empty(AIPresetStore.Presets);
		Assert.Null(AIPresetStore.DefaultPreset);
	}

	[Fact]
	public void TryLoad_BadJson_Fails() {
		File.WriteAllText(_path, "{ not json");

		Assert.False(AIPresetStore.TryLoad(_path, out var errors));

		Assert.Contains(errors, static e => e.Contains(L.Prefix("AIPresetsReadFailed")));
	}

	[Fact]
	public void TryLoad_NullRoot_Fails() {
		File.WriteAllText(_path, "null");

		Assert.False(AIPresetStore.TryLoad(_path, out var errors));

		Assert.Contains(errors, static e => e.Contains(L.Prefix("AIPresetsReadFailed")));
	}

	[Fact]
	public void TryLoad_ValidDocument_LoadsFirstAsDefault() {
		File.WriteAllText(_path, """
			{
			  "MaxParallel": 4,
			  "Presets": [
			    { "Name": "first", "Provider": "openai", "ApiKey": "k", "BaseUrl": "https://api.example.com/v1", "Model": "m1" },
			    { "Name": "second", "Provider": "gemini", "ApiKey": "g", "Model": "m2" }
			  ]
			}
			""");

		Assert.True(AIPresetStore.TryLoad(_path, out _));

		Assert.Equal(4, AIPresetStore.MaxParallel);
		Assert.Equal("first", AIPresetStore.DefaultPreset?.Name);
		Assert.Equal("second", AIPresetStore.Find("second")?.Name);
		Assert.Equal(AIPresetProvider.Gemini, AIPresetStore.Find("second")?.Provider);
	}

	[Fact]
	public void TryLoad_FindByName_FallsBackToDefaultWhenBlank() {
		File.WriteAllText(_path, """
			{ "MaxParallel": 5, "Presets": [{ "Name": "first", "Provider": "openai", "ApiKey": "k", "BaseUrl": "https://a.example.com", "Model": "m" }] }
			""");
		Assert.True(AIPresetStore.TryLoad(_path, out _));

		Assert.Equal("first", AIPresetStore.Find(null)?.Name);
		Assert.Equal("first", AIPresetStore.Find("")?.Name);
		Assert.Null(AIPresetStore.Find("nope"));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void TryLoad_NonPositiveMaxParallel_Fails(int maxParallel) {
		File.WriteAllText(_path, $$"""
			{ "MaxParallel": {{maxParallel}}, "Presets": [] }
			""");

		Assert.False(AIPresetStore.TryLoad(_path, out var errors));

		Assert.Contains(errors, static e => e.Contains(L.Prefix("AIMaxParallelInvalid")));
	}

	[Fact]
	public void Validate_EmptyName_Rejected() {
		Assert.Contains(AIPresetStore.ValidatePreset(new AIPreset { Name = "  ", Provider = AIPresetProvider.OpenAI }, [], null),
			static e => e.Contains(L.Get("AIPresetNameRequired")));
	}

	[Fact]
	public void Validate_DuplicateName_Rejected() {
		var problems = AIPresetStore.ValidatePreset(OpenAi(), [OpenAi()], null);

		Assert.Contains(problems, static e => e.Contains(L.Prefix("AIPresetDuplicateName")));
	}

	[Fact]
	public void Validate_DuplicateName_AllowsSameIndex() {
		var problems = AIPresetStore.ValidatePreset(OpenAi(), [OpenAi()], 0);

		Assert.DoesNotContain(problems, static e => e.Contains(L.Prefix("AIPresetDuplicateName")));
	}

	[Fact]
	public void Validate_OpenAi_RequiresKeyBaseUrlAndModel() {
		var problems = AIPresetStore.ValidatePreset(new AIPreset { Name = "x", Provider = AIPresetProvider.OpenAI }, [], null);

		Assert.Contains(problems, static e => e.Contains(L.Get("AIPresetApiKeyRequired")));
		Assert.Contains(problems, static e => e.Contains(L.Get("AIPresetModelRequired")));
		Assert.Contains(problems, static e => e.Contains(L.Get("AIPresetBaseUrlRequired")));
	}

	[Theory]
	[InlineData("ftp://api.example.com")]
	[InlineData("not-a-url")]
	[InlineData("file:///tmp/x")]
	public void Validate_OpenAi_RejectsNonHttpBaseUrl(string baseUrl) {
		var preset = Copy(OpenAi(), baseUrl: baseUrl);

		var problems = AIPresetStore.ValidatePreset(preset, [], null);

		Assert.Contains(problems, static e => e.Contains(L.Prefix("AIPresetBaseUrlInvalid")));
	}

	[Theory]
	[InlineData("http://api.example.com")]
	[InlineData("https://api.example.com")]
	public void Validate_OpenAi_AcceptsHttpSchemes(string baseUrl) {
		var preset = Copy(OpenAi(), baseUrl: baseUrl);

		Assert.Empty(AIPresetStore.ValidatePreset(preset, [], null));
	}

	[Fact]
	public void Validate_Gemini_RequiresKeyAndModel() {
		var problems = AIPresetStore.ValidatePreset(new AIPreset { Name = "g", Provider = AIPresetProvider.Gemini }, [], null);

		Assert.Contains(problems, static e => e.Contains(L.Get("AIPresetApiKeyRequired")));
		Assert.Contains(problems, static e => e.Contains(L.Get("AIPresetModelRequired")));
	}

	[Fact]
	public void Validate_Gemini_DoesNotRequireBaseUrl() {
		var preset = new AIPreset { Name = "g", Provider = AIPresetProvider.Gemini, ApiKey = "k", Model = "m" };

		Assert.Empty(AIPresetStore.ValidatePreset(preset, [], null));
	}

	[Fact]
	public void Validate_Vertex_RequiresProjectLocationAndModel() {
		var problems = AIPresetStore.ValidatePreset(new AIPreset { Name = "v", Provider = AIPresetProvider.Vertex }, [], null);

		Assert.Contains(problems, static e => e.Contains(L.Get("AIPresetVertexFieldsRequired")));
		Assert.Contains(problems, static e => e.Contains(L.Get("AIPresetModelRequired")));
	}

	[Fact]
	public void Validate_Vertex_AcceptsCompleteFields() {
		var preset = new AIPreset { Name = "v", Provider = AIPresetProvider.Vertex, Project = "p", Location = "us-central1", Model = "m" };

		Assert.Empty(AIPresetStore.ValidatePreset(preset, [], null));
	}

	[Fact]
	public void Validate_Vertex_RequiresBothProjectAndLocation() {
		var preset = new AIPreset { Name = "v", Provider = AIPresetProvider.Vertex, Project = "p", Model = "m" };

		Assert.Contains(AIPresetStore.ValidatePreset(preset, [], null), static e => e.Contains(L.Get("AIPresetVertexFieldsRequired")));
	}

	[Fact]
	public void Validate_InvalidProvider_Rejected() {
		var problems = AIPresetStore.ValidatePreset(new AIPreset { Name = "x", Provider = (AIPresetProvider)99 }, [], null);

		Assert.Contains(problems, static e => e.Contains(L.Prefix("AIPresetProviderInvalid")));
	}

	[Fact]
	public void Validate_SamplingBounds_Rejected() {
		var preset = Copy(OpenAi(), temperature: -1f, topP: -1f, maxOutputTokens: 0);

		var problems = AIPresetStore.ValidatePreset(preset, [], null);

		Assert.Contains(problems, static e => e.Contains(L.Prefix("AITemperatureInvalid")));
		Assert.Contains(problems, static e => e.Contains(L.Prefix("AITopPInvalid")));
		Assert.Contains(problems, static e => e.Contains(L.Prefix("AIMaxOutputTokensInvalid")));
	}

	[Fact]
	public void ValidateDocument_CollectsEveryPresetProblem() {
		var problems = AIPresetStore.ValidateDocument(new AIPresetDocument {
			MaxParallel = 0,
			Presets = [OpenAi(), OpenAi()],
		});

		Assert.Contains(problems, static e => e.Contains(L.Prefix("AIMaxParallelInvalid")));
		Assert.Contains(problems, static e => e.Contains(L.Prefix("AIPresetDuplicateName")));
	}

	[Fact]
	public void Add_AppendsAndPersists() {
		Assert.True(AIPresetStore.TryLoad(WriteDocument(), out _));
		var added = OpenAi("second", "m2");

		Assert.True(AIPresetStore.AddPatch(added, null, out var result));
		Assert.Null(result.Error);
		Assert.Equal(2, AIPresetStore.Presets.Count);

		var reloaded = JsonHelperRoundTrip();
		Assert.Equal(["prod", "second"], reloaded.Presets.Select(preset => preset.Name));
		Assert.Equal(2, reloaded.Presets.Count);
	}

	[Fact]
	public void Add_Invalid_RejectedWithoutPersisting() {
		Assert.True(AIPresetStore.TryLoad(WriteDocument(), out _));

		Assert.False(AIPresetStore.AddPatch(new AIPreset { Name = "bad", Provider = AIPresetProvider.OpenAI }, null, out var result));
		Assert.NotNull(result.Error);
		Assert.Single(AIPresetStore.Presets);
	}

	[Fact]
	public void Add_DuplicateName_Rejected() {
		Assert.True(AIPresetStore.TryLoad(WriteDocument(), out _));

		Assert.False(AIPresetStore.AddPatch(OpenAi(), null, out var result));
		Assert.Contains(L.Prefix("AIPresetDuplicateName"), result.Error);
	}

	[Fact]
	public void Apply_ChangesFieldsAndPersists() {
		Assert.True(AIPresetStore.TryLoad(WriteDocument(), out _));

		Assert.True(AIPresetStore.Apply("prod", new AIPresetPatch { Model = Patch<string>.SetTo("m-updated") }, null, out var result));
		Assert.Null(result.Error);

		var reloaded = JsonHelperRoundTrip();
		Assert.Equal("m-updated", reloaded.Presets[0].Model);
	}

	[Fact]
	public void Apply_Rename_PersistsNewName() {
		Assert.True(AIPresetStore.TryLoad(WriteDocument(), out _));

		Assert.True(AIPresetStore.Apply("prod", new AIPresetPatch { Name = Patch<string>.SetTo("renamed") }, null, out _));

		Assert.Null(AIPresetStore.Find("prod"));
		Assert.Equal("renamed", AIPresetStore.Find("renamed")?.Name);
		Assert.Equal(["renamed"], JsonHelperRoundTrip().Presets.Select(preset => preset.Name));
	}

	[Fact]
	public void Apply_UnknownName_Fails() {
		Assert.True(AIPresetStore.TryLoad(WriteDocument(), out _));

		Assert.False(AIPresetStore.Apply("nope", new AIPresetPatch { Model = Patch<string>.SetTo("m2") }, null, out var result));
		Assert.Contains(L.Prefix("AIPresetNotFound"), result.Error);
	}

	[Fact]
	public void Apply_RenameWhileReferenced_Rejected() {
		Assert.True(AIPresetStore.TryLoad(WriteDocument(), out _));
		AIPresetUsageRegistry.LoadForTest([new AIPresetUsageEntry("FakeModule", "Caller#Go", AIPresetDependency.Named("prod"))]);

		Assert.False(AIPresetStore.Apply("prod", new AIPresetPatch { Name = Patch<string>.SetTo("renamed") }, null, out var result));
		Assert.Contains(L.Prefix("AIPresetInUse"), result.Error);
		Assert.Contains("FakeModule", result.Error);
		Assert.Contains("Caller#Go", result.Error);
		Assert.NotNull(AIPresetStore.Find("prod"));
	}

	[Fact]
	public void Apply_FieldChangeWhileReferenced_Allowed() {
		Assert.True(AIPresetStore.TryLoad(WriteDocument(), out _));
		AIPresetUsageRegistry.LoadForTest([new AIPresetUsageEntry("FakeModule", "Caller#Go", AIPresetDependency.Named("prod"))]);

		Assert.True(AIPresetStore.Apply("prod", new AIPresetPatch { Model = Patch<string>.SetTo("m-new") }, null, out var result));
		Assert.Null(result.Error);
		Assert.Equal("m-new", AIPresetStore.Find("prod")?.Model);
	}

	[Fact]
	public void Remove_DeletesAndPersists() {
		Assert.True(AIPresetStore.TryLoad(WriteDocument(), out _));

		Assert.True(AIPresetStore.RemovePatch("prod", null, out var result));
		Assert.Null(result.Error);
		Assert.Empty(AIPresetStore.Presets);
		Assert.Empty(JsonHelperRoundTrip().Presets);
	}

	[Fact]
	public void Remove_UnknownName_Fails() {
		Assert.True(AIPresetStore.TryLoad(WriteDocument(), out _));

		Assert.False(AIPresetStore.RemovePatch("nope", null, out var result));
		Assert.Contains(L.Prefix("AIPresetNotFound"), result.Error);
	}

	[Fact]
	public void Remove_WhileReferenced_Rejected() {
		Assert.True(AIPresetStore.TryLoad(WriteDocument(), out _));
		AIPresetUsageRegistry.LoadForTest([new AIPresetUsageEntry("FakeModule", "Caller#Go", AIPresetDependency.Named("prod"))]);

		Assert.False(AIPresetStore.RemovePatch("prod", null, out var result));
		Assert.Contains(L.Prefix("AIPresetInUse"), result.Error);
		Assert.Contains("FakeModule", result.Error);
		Assert.Single(AIPresetStore.Presets);
	}

	[Fact]
	public void Add_SaveFailure_ReportsAndKeepsSnapshot() {
		Assert.True(AIPresetStore.TryLoad(WriteDocument(), out _));
		using var locked = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);

		Assert.False(AIPresetStore.AddPatch(OpenAi("second", "m2"), null, out var result));

		Assert.Equal(L.Get("AIPresetsSaveFailed"), result.Error);
		Assert.Single(AIPresetStore.Presets);
	}

	[Fact]
	public void Remove_SaveFailure_ReportsAndKeepsSnapshot() {
		Assert.True(AIPresetStore.TryLoad(WriteDocument(), out _));
		using var locked = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);

		Assert.False(AIPresetStore.RemovePatch("prod", null, out var result));

		Assert.Equal(L.Get("AIPresetsSaveFailed"), result.Error);
		Assert.Single(AIPresetStore.Presets);
	}

	[Fact]
	public void DefaultPath_LivesInConfigDirectory() {
		Assert.EndsWith(Path.Combine("config", "aipresets.json"), AIPresetStore.DefaultPath);
	}

	[Fact]
	public void Document_SurvivesFailedLoad() {
		File.WriteAllText(_path, """
			{ "MaxParallel": 7, "Presets": [{ "Name": "kept", "Provider": "openai", "ApiKey": "k", "BaseUrl": "https://a.example.com", "Model": "m" }] }
			""");
		Assert.True(AIPresetStore.TryLoad(_path, out _));

		File.WriteAllText(_path, "{ broken");
		Assert.False(AIPresetStore.TryLoad(_path, out _));

		Assert.Equal(7, AIPresetStore.MaxParallel);
		Assert.Equal("kept", AIPresetStore.DefaultPreset?.Name);
	}

	private string WriteDocument() {
		File.WriteAllText(_path, """
			{ "MaxParallel": 5, "Presets": [{ "Name": "prod", "Provider": "openai", "ApiKey": "k", "BaseUrl": "https://api.example.com/v1", "Model": "m1" }] }
			""");
		return _path;
	}

	[Fact]
	public void Apply_LegacyConnection_LoadsTopLevelKeyAndSavesOneCopy() {
		File.WriteAllText(_path, """
			{ "MaxParallel": 5, "Presets": [{ "Name": "prod", "Provider": "openai", "ApiKey": "current-key", "BaseUrl": "https://api.example.com/v1", "Model": "m1", "Connection": { "ApiKey": "stale-key" } }] }
			""");
		Assert.True(AIPresetStore.TryLoad(_path, out var errors), string.Join("; ", errors));
		Assert.Equal("current-key", AIPresetStore.Find("prod")?.Connection.ApiKey);

		Assert.True(AIPresetStore.Apply("prod", new AIPresetPatch { Model = Patch<string>.SetTo("m2") }, null, out var result), result.Error);

		using var saved = JsonDocument.Parse(File.ReadAllText(_path));
		var preset = saved.RootElement.GetProperty("Presets")[0];
		Assert.Equal("current-key", preset.GetProperty("ApiKey").GetString());
		Assert.False(preset.TryGetProperty("Connection", out _));
		Assert.DoesNotContain("stale-key", File.ReadAllText(_path));
		Assert.True(AIPresetStore.TryLoad(_path, out errors), string.Join("; ", errors));
		Assert.Equal("m2", AIPresetStore.Find("prod")?.Model);
	}

	[Theory]
	[InlineData(AIPresetProvider.Gemini)]
	[InlineData(AIPresetProvider.Vertex)]
	public void TryLoad_GoogleUnofficialEndpoint_KeepsPublishedPreset(AIPresetProvider provider) {
		Assert.True(AIPresetStore.TryLoad(WriteDocument(), out _));
		var source = JsonSerializer.Serialize(new AIPresetDocument { Presets = [GooglePreset(provider, "https://untrusted.example.com")] }, JsonHelper.Options);
		File.WriteAllText(_path, source);

		Assert.False(AIPresetStore.TryLoad(_path, out var errors));

		Assert.Contains(errors, static error => error.Contains(L.Prefix("AIPresetBaseUrlInvalid")));
		Assert.Equal("prod", AIPresetStore.DefaultPreset?.Name);
		Assert.Equal(source, File.ReadAllText(_path));
	}

	[Theory]
	[InlineData(AIPresetProvider.Gemini)]
	[InlineData(AIPresetProvider.Vertex)]
	public void Apply_GoogleUnofficialEndpoint_LeavesFileAndSnapshotUnchanged(AIPresetProvider provider) {
		File.WriteAllText(_path, JsonSerializer.Serialize(new AIPresetDocument { Presets = [GooglePreset(provider, "")] }, JsonHelper.Options));
		Assert.True(AIPresetStore.TryLoad(_path, out _));
		var original = File.ReadAllText(_path);
		var version = AIPresetStore.Snapshot.Version;

		Assert.False(AIPresetStore.Apply("google", new AIPresetPatch { BaseUrl = Patch<string>.SetTo("https://untrusted.example.com") }, null, out var result));

		Assert.Contains(L.Prefix("AIPresetBaseUrlInvalid"), result.Error);
		Assert.Equal(version, AIPresetStore.Snapshot.Version);
		Assert.Equal("", AIPresetStore.Find("google")?.BaseUrl);
		Assert.Equal(original, File.ReadAllText(_path));
	}

	private static AIPreset GooglePreset(AIPresetProvider provider, string baseUrl) {
		return new AIPreset { Name = "google", Provider = provider, ApiKey = "fake-key", BaseUrl = baseUrl, Project = "project", Location = "us-central1", Model = "model" };
	}

	private AIPresetDocument JsonHelperRoundTrip() {
		return Pengin1011.Helper.JsonHelper.Load<AIPresetDocument>(_path);
	}
}
