using Pengin1011;
using Pengin1011.Core;
using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class PresetCliTests : IDisposable {
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"qq1011_cli_preset_{Guid.NewGuid():N}");
	private readonly string _path;

	public PresetCliTests() {
		Directory.CreateDirectory(_directory);
		_path = Path.Combine(_directory, "aipresets.json");
		AIPresetUsageRegistry.ForgetAll();
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
	public async Task Help_ListsPresetCommands() {
		var result = await CliDispatcher.ExecuteAsync("help");

		Assert.Contains("preset list", result.Output);
		Assert.Contains("preset add", result.Output);
		Assert.Contains("preset remove", result.Output);
		Assert.DoesNotContain("reload", result.Output);
	}

	[Fact]
	public async Task Preset_WithoutSubcommand_ShowsUsage() {
		var result = await CliDispatcher.ExecuteAsync("preset");

		Assert.Contains(L.Prefix("CliPresetUsage"), result.Output);
	}

	[Fact]
	public async Task Preset_UnknownSubcommand_ShowsUsage() {
		var result = await CliDispatcher.ExecuteAsync("preset nope");

		Assert.Contains(L.Prefix("CliPresetUsage"), result.Output);
	}

	[Fact]
	public async Task PresetList_Empty_ReportsNone() {
		Seed();

		var result = await CliDispatcher.ExecuteAsync("preset list");

		Assert.Contains(L.Get("CliPresetNone"), result.Output);
	}

	[Fact]
	public async Task PresetList_ShowsEveryPresetWithDefaultMarker() {
		Seed("prod", "dev");

		var result = await CliDispatcher.ExecuteAsync("preset list");

		Assert.Contains("prod", result.Output);
		Assert.Contains("dev", result.Output);
		Assert.Contains(L.Get("CliPresetDefaultSuffix").Trim(), result.Output);
	}

	[Fact]
	public async Task PresetStatus_ShowsPresetCountAndDefault() {
		Seed("prod");

		var result = await CliDispatcher.ExecuteAsync("status");

		Assert.Contains("prod", result.Output);
	}

	[Fact]
	public async Task PresetStatus_WithoutPresets_ShowsPlaceholder() {
		Seed();

		var result = await CliDispatcher.ExecuteAsync("status");

		Assert.Contains(L.Get("AIModelNotConfigured"), result.Output);
	}

	[Fact]
	public async Task PresetShow_UnknownName_ReportsNotFound() {
		Seed("prod");

		var result = await CliDispatcher.ExecuteAsync("preset show absent");

		Assert.Contains(L.Prefix("AIPresetNotFound"), result.Output);
	}

	[Fact]
	public async Task PresetShow_ListsEveryField() {
		Seed("prod");

		var result = await CliDispatcher.ExecuteAsync("preset show prod");

		Assert.Contains("name", result.Output);
		Assert.Contains("provider", result.Output);
		Assert.Contains("apikey", result.Output);
		Assert.Contains("baseurl", result.Output);
		Assert.Contains("model", result.Output);
		Assert.Contains("temperature", result.Output);
	}

	[Fact]
	public async Task PresetShow_EmptyField_RendersDash() {
		Seed("prod");

		var result = await CliDispatcher.ExecuteAsync("preset show prod");

		Assert.Contains("project", result.Output);
		Assert.Contains("-", result.Output);
	}

	[Fact]
	public async Task PresetAdd_PersistsPreset() {
		Seed();

		var result = await CliDispatcher.ExecuteAsync("preset add prod openai apikey=k baseurl=https://api.example.com/v1 model=m");

		Assert.Contains(L.Prefix("CliPresetAdded"), result.Output);
		Assert.Equal("prod", AIPresetStore.DefaultPreset?.Name);
		Assert.Equal("m", AIPresetStore.DefaultPreset?.Model);
	}

	[Fact]
	public async Task PresetAdd_InvalidProvider_Rejected() {
		Seed();

		var result = await CliDispatcher.ExecuteAsync("preset add prod claude apikey=k");

		Assert.Contains(L.Prefix("CliPresetProviderInvalid"), result.Output);
		Assert.Empty(AIPresetStore.Presets);
	}

	[Fact]
	public async Task PresetAdd_UnknownField_Rejected() {
		Seed();

		var result = await CliDispatcher.ExecuteAsync("preset add prod openai nope=1");

		Assert.Contains(L.Prefix("CliPresetFieldUnknown"), result.Output);
		Assert.Empty(AIPresetStore.Presets);
	}

	[Fact]
	public async Task PresetAdd_MalformedPair_Rejected() {
		Seed();

		var result = await CliDispatcher.ExecuteAsync("preset add prod openai apikey");

		Assert.Contains(L.Prefix("CliPresetFieldInvalid"), result.Output);
		Assert.Empty(AIPresetStore.Presets);
	}

	[Fact]
	public async Task PresetAdd_BadNumber_Rejected() {
		Seed();

		var result = await CliDispatcher.ExecuteAsync("preset add prod openai model=m temperature=abc");

		Assert.Contains(L.Prefix("CliPresetFieldInvalid"), result.Output);
		Assert.Empty(AIPresetStore.Presets);
	}

	[Fact]
	public async Task PresetAdd_SamplingFields_Persist() {
		Seed();

		await CliDispatcher.ExecuteAsync("preset add prod openai apikey=k baseurl=https://api.example.com/v1 model=m temperature=0.7 topp=0.9 maxoutputtokens=123");

		var preset = AIPresetStore.DefaultPreset!;
		Assert.Equal(0.7f, preset.Temperature);
		Assert.Equal(0.9f, preset.TopP);
		Assert.Equal(123, preset.MaxOutputTokens);
	}

	[Fact]
	public async Task PresetAdd_InvalidPreset_ReportsStoreError() {
		Seed();

		var result = await CliDispatcher.ExecuteAsync("preset add prod openai apikey=k");

		Assert.Contains(L.Prefix("AIPresetModelRequired"), result.Output);
		Assert.Empty(AIPresetStore.Presets);
	}

	[Fact]
	public async Task PresetSet_ChangesSingleField() {
		Seed("prod");

		var result = await CliDispatcher.ExecuteAsync("preset set prod model=m-new");

		Assert.Contains(L.Prefix("CliPresetUpdated"), result.Output);
		Assert.Equal("m-new", AIPresetStore.DefaultPreset?.Model);
	}

	[Fact]
	public async Task PresetSet_RenamesThroughNameField() {
		Seed("prod");

		var result = await CliDispatcher.ExecuteAsync("preset set prod name=renamed");

		Assert.Contains(L.Prefix("CliPresetUpdated"), result.Output);
		Assert.Null(AIPresetStore.Find("prod"));
		Assert.Equal("renamed", AIPresetStore.DefaultPreset?.Name);
	}

	[Fact]
	public async Task PresetSet_UnknownName_ReportsNotFound() {
		Seed("prod");

		var result = await CliDispatcher.ExecuteAsync("preset set absent model=m");

		Assert.Contains(L.Prefix("AIPresetNotFound"), result.Output);
	}

	[Fact]
	public async Task PresetSet_WithoutFields_ShowsUsage() {
		Seed("prod");

		var result = await CliDispatcher.ExecuteAsync("preset set prod");

		Assert.Contains(L.Prefix("CliPresetUsage"), result.Output);
	}

	[Fact]
	public async Task PresetSet_EmptyNewName_Rejected() {
		Seed("prod");

		var result = await CliDispatcher.ExecuteAsync("preset set prod name=");

		Assert.Contains(L.Prefix("AIPresetNameRequired"), result.Output);
		Assert.NotNull(AIPresetStore.Find("prod"));
	}

	[Fact]
	public async Task PresetSet_InvalidProvider_Rejected() {
		Seed("prod");

		var result = await CliDispatcher.ExecuteAsync("preset set prod provider=nope");

		Assert.Contains(L.Prefix("CliPresetProviderInvalid"), result.Output);
		Assert.Equal(AIPresetProvider.OpenAI, AIPresetStore.DefaultPreset?.Provider);
	}

	[Fact]
	public async Task PresetSet_UnknownField_Rejected() {
		Seed("prod");

		var result = await CliDispatcher.ExecuteAsync("preset set prod nope=1");

		Assert.Contains(L.Prefix("CliPresetFieldUnknown"), result.Output);
	}

	[Fact]
	public async Task PresetSet_KeepsUntouchedFields() {
		Seed("prod");

		await CliDispatcher.ExecuteAsync("preset set prod model=m-new");

		var preset = AIPresetStore.DefaultPreset!;
		Assert.Equal("k", preset.ApiKey);
		Assert.Equal("https://api.example.com/v1", preset.BaseUrl);
		Assert.Equal("prod", preset.Name);
	}

	[Fact]
	public async Task PresetRemove_Persists() {
		Seed("prod");

		var result = await CliDispatcher.ExecuteAsync("preset remove prod");

		Assert.Contains(L.Prefix("CliPresetRemoved"), result.Output);
		Assert.Empty(AIPresetStore.Presets);
	}

	[Fact]
	public async Task PresetRemove_UnknownName_ReportsNotFound() {
		Seed("prod");

		var result = await CliDispatcher.ExecuteAsync("preset remove absent");

		Assert.Contains(L.Prefix("AIPresetNotFound"), result.Output);
		Assert.Single(AIPresetStore.Presets);
	}

	[Fact]
	public async Task PresetRemove_WhileReferenced_Rejected() {
		Seed("prod");
		AIPresetUsageRegistry.LoadForTest([new AIPresetUsageEntry("FakeModule", "Caller#Go", AIPresetDependency.Named("prod"))]);

		var result = await CliDispatcher.ExecuteAsync("preset remove prod");

		Assert.Contains(L.Prefix("AIPresetInUse"), result.Output);
		Assert.Contains("FakeModule", result.Output);
		Assert.Single(AIPresetStore.Presets);
	}

	[Fact]
	public async Task PresetRemove_RenameWhileReferenced_Rejected() {
		Seed("prod");
		AIPresetUsageRegistry.LoadForTest([new AIPresetUsageEntry("FakeModule", "Caller#Go", AIPresetDependency.Named("prod"))]);

		var result = await CliDispatcher.ExecuteAsync("preset set prod name=renamed");

		Assert.Contains(L.Prefix("AIPresetInUse"), result.Output);
		Assert.NotNull(AIPresetStore.Find("prod"));
	}

	[Fact]
	public async Task PresetAdd_SaveFailure_ReportsError() {
		Seed();
		using var locked = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);

		var result = await CliDispatcher.ExecuteAsync("preset add prod openai apikey=k baseurl=https://api.example.com/v1 model=m");

		Assert.Contains(L.Get("AIPresetsSaveFailed"), result.Output);
		Assert.Empty(AIPresetStore.Presets);
	}
}
