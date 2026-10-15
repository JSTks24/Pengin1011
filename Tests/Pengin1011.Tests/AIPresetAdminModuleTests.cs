using Pengin1011.Core.Localization;
using Pengin1011.Modules.AIPresetAdmin;
using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class AIPresetAdminModuleTests : IDisposable {
	private static readonly System.Reflection.Assembly _module = typeof(AIPresetAdminLogic).Assembly;

	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"qq1011_module_preset_{Guid.NewGuid():N}");
	private readonly string _path;

	public AIPresetAdminModuleTests() {
		Directory.CreateDirectory(_directory);
		_path = Path.Combine(_directory, "aipresets.json");
		AIPresetUsageRegistry.ForgetAll();
		Localizer.RegisterModule(_module);
	}

	private static string ModuleText(string key) {
		return Localizer.Get(_module, key);
	}

	private static string ModulePrefix(string key) {
		var value = ModuleText(key);
		var index = value.IndexOf('{');
		return index < 0 ? value : value[..index].TrimEnd();
	}

	public void Dispose() {
		AIPresetStore.ResetForTest();
		AIPresetUsageRegistry.ForgetAll();
		try { Directory.Delete(_directory, true); } catch (IOException) { }
	}

	private void Seed(params string[] presets) {
		var entries = presets.Length == 0
			? ""
			: string.Join(",", presets.Select(name => $$"""{ "Name": "{{name}}", "Provider": "openai", "ApiKey": "k", "BaseUrl": "https://api.example.com/v1", "Model": "m" }"""));
		File.WriteAllText(_path, $$"""{ "MaxParallel": 5, "Presets": [{{entries}}] }""");
		Assert.True(AIPresetStore.TryLoad(_path, out _));
	}

	[Fact]
	public void List_Empty_ReportsNone() {
		Seed();

		Assert.Contains(ModulePrefix("PresetReplyEmpty"), AIPresetAdminLogic.List());
	}

	[Fact]
	public void List_ShowsDefaultMarkerOnFirstPreset() {
		Seed("prod", "dev");

		var text = AIPresetAdminLogic.List();

		Assert.Contains("prod", text);
		Assert.Contains("dev", text);
		Assert.Contains(ModuleText("PresetReplyDefaultSuffix").Trim(), text);
	}

	[Fact]
	public void Show_UnknownName_ReportsNotFound() {
		Seed("prod");

		Assert.Contains(ModulePrefix("PresetReplyNotFound"), AIPresetAdminLogic.Show("absent"));
	}

	[Fact]
	public void Show_ListsEveryField() {
		Seed("prod");

		var text = AIPresetAdminLogic.Show("prod");

		foreach (var field in new[] { "name", "provider", "apikey", "baseurl", "model", "temperature" }) {
			Assert.Contains(field, text);
		}
	}

	[Fact]
	public void Add_OpenAi_Persists() {
		Seed();

		var result = AIPresetAdminLogic.Add("prod", "openai", new AIPresetAddFields(ApiKey: "k", BaseUrl: "https://api.example.com/v1", Model: "m"));

		Assert.Contains(ModulePrefix("PresetReplyAdded"), result);
		Assert.Equal("prod", AIPresetStore.DefaultPreset?.Name);
	}

	[Fact]
	public void Add_InvalidProvider_Rejected() {
		Seed();

		var result = AIPresetAdminLogic.Add("prod", "claude", new AIPresetAddFields());

		Assert.Contains(ModulePrefix("PresetReplyProviderInvalid"), result);
		Assert.Empty(AIPresetStore.Presets);
	}

	[Fact]
	public void Add_IncompletePreset_ReportsStoreError() {
		Seed();

		var result = AIPresetAdminLogic.Add("prod", "openai", new AIPresetAddFields(ApiKey: "k"));

		Assert.Contains(L.Prefix("AIPresetModelRequired"), result);
		Assert.Empty(AIPresetStore.Presets);
	}

	[Fact]
	public void Add_Vertex_PersistsProjectAndLocation() {
		Seed();

		AIPresetAdminLogic.Add("v", "vertex", new AIPresetAddFields(Project: "p", Location: "us-central1", Model: "m"));

		var preset = AIPresetStore.DefaultPreset!;
		Assert.Equal(AIPresetProvider.Vertex, preset.Provider);
		Assert.Equal("p", preset.Project);
		Assert.Equal("us-central1", preset.Location);
	}

	[Fact]
	public void Set_ChangesSingleFieldAndKeepsOthers() {
		Seed("prod");

		var result = AIPresetAdminLogic.Set("prod", "model", "m-new");

		Assert.Contains(ModulePrefix("PresetReplyUpdated"), result);
		var preset = AIPresetStore.DefaultPreset!;
		Assert.Equal("m-new", preset.Model);
		Assert.Equal("k", preset.ApiKey);
		Assert.Equal("prod", preset.Name);
	}

	[Fact]
	public void Set_Name_Renames() {
		Seed("prod");

		AIPresetAdminLogic.Set("prod", "name", "renamed");

		Assert.Null(AIPresetStore.Find("prod"));
		Assert.Equal("renamed", AIPresetStore.DefaultPreset?.Name);
	}

	[Fact]
	public void Set_EmptyName_Rejected() {
		Seed("prod");

		Assert.Contains(L.Prefix("AIPresetNameRequired"), AIPresetAdminLogic.Set("prod", "name", " "));
		Assert.NotNull(AIPresetStore.Find("prod"));
	}

	[Fact]
	public void Set_UnknownName_ReportsNotFound() {
		Seed("prod");

		Assert.Contains(ModulePrefix("PresetReplyNotFound"), AIPresetAdminLogic.Set("absent", "model", "m"));
	}

	[Fact]
	public void Set_UnknownField_Rejected() {
		Seed("prod");

		Assert.Contains(ModulePrefix("PresetReplyFieldUnknown"), AIPresetAdminLogic.Set("prod", "nope", "1"));
	}

	[Fact]
	public void Set_InvalidProvider_Rejected() {
		Seed("prod");

		Assert.Contains(ModulePrefix("PresetReplyProviderInvalid"), AIPresetAdminLogic.Set("prod", "provider", "nope"));
		Assert.Equal(AIPresetProvider.OpenAI, AIPresetStore.DefaultPreset?.Provider);
	}

	[Fact]
	public void Set_BadNumber_Rejected() {
		Seed("prod");

		Assert.Contains(ModulePrefix("PresetReplyValueInvalid"), AIPresetAdminLogic.Set("prod", "temperature", "abc"));
		Assert.Null(AIPresetStore.DefaultPreset?.Temperature);
	}

	[Theory]
	[InlineData("temperature", "0.7")]
	[InlineData("topp", "0.9")]
	[InlineData("maxoutputtokens", "123")]
	public void Set_NumericFields_ParseInvariant(string field, string value) {
		Seed("prod");

		AIPresetAdminLogic.Set("prod", field, value);

		var preset = AIPresetStore.DefaultPreset!;
		Assert.NotNull(field switch {
			"temperature" => preset.Temperature,
			"topp" => preset.TopP,
			_ => preset.MaxOutputTokens,
		});
	}

	[Theory]
	[InlineData("apikey", "k2")]
	[InlineData("baseurl", "https://other.example.com/v1")]
	[InlineData("project", "p")]
	[InlineData("location", "l")]
	[InlineData("model", "m2")]
	public void Set_TextFields_Apply(string field, string value) {
		Seed("prod");

		AIPresetAdminLogic.Set("prod", field, value);

		var preset = AIPresetStore.DefaultPreset!;
		var actual = field switch {
			"apikey" => preset.ApiKey,
			"baseurl" => preset.BaseUrl,
			"project" => preset.Project,
			"location" => preset.Location,
			_ => preset.Model,
		};
		Assert.Equal(value, actual);
	}

	[Fact]
	public void Set_FieldNameIsCaseInsensitive() {
		Seed("prod");

		AIPresetAdminLogic.Set("prod", "MODEL", "m-new");

		Assert.Equal("m-new", AIPresetStore.DefaultPreset?.Model);
	}

	[Fact]
	public void Remove_Persists() {
		Seed("prod");

		Assert.Contains(ModulePrefix("PresetReplyRemoved"), AIPresetAdminLogic.Remove("prod"));
		Assert.Empty(AIPresetStore.Presets);
	}

	[Fact]
	public void Remove_UnknownName_ReportsNotFound() {
		Seed("prod");

		Assert.Contains(L.Prefix("AIPresetNotFound"), AIPresetAdminLogic.Remove("absent"));
	}

	[Fact]
	public void Remove_WhileReferenced_Rejected() {
		Seed("prod");
		AIPresetUsageRegistry.LoadForTest([new AIPresetUsageEntry("FakeModule", "Caller#Go", AIPresetDependency.Named("prod"))]);

		var result = AIPresetAdminLogic.Remove("prod");

		Assert.Contains(L.Prefix("AIPresetInUse"), result);
		Assert.Contains("FakeModule", result);
		Assert.Single(AIPresetStore.Presets);
	}

	[Fact]
	public void Fields_CoverEveryEditableField() {
		Assert.Equal(
			["name", "provider", "apikey", "baseurl", "project", "location", "model", "temperature", "topp", "maxoutputtokens"],
			AIPresetAdminLogic.Fields);
	}
}
