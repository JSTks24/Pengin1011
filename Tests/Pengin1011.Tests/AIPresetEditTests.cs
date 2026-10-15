using Pengin1011.Helper;
using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class AIPresetEditTests : IDisposable {
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"qq1011_edit_{Guid.NewGuid():N}");
	private readonly string _path;

	public AIPresetEditTests() {
		Directory.CreateDirectory(_directory);
		_path = Path.Combine(_directory, "aipresets.json");
		AIPresetUsageRegistry.ForgetAll();
	}

	public void Dispose() {
		AIPresetStore.ResetForTest();
		AIPresetUsageRegistry.ForgetAll();
		try { Directory.Delete(_directory, true); } catch (IOException) { }
	}

	private void Seed(params string[] names) {
		var entries = string.Join(",", names.Select(name => $$"""{ "Name": "{{name}}", "Provider": "openai", "ApiKey": "k", "BaseUrl": "https://api.example.com/v1", "Model": "m" }"""));
		File.WriteAllText(_path, $$"""{ "MaxParallel": 5, "Presets": [{{entries}}] }""");
		Assert.True(AIPresetStore.TryLoad(_path, out _));
	}

	private static AIPresetPatch Patch(string? model = null, string? apiKey = null, AIPresetProvider? provider = null) {
		return new AIPresetPatch {
			Model = model == null ? Patch<string>.Absent : Patch<string>.SetTo(model),
			ApiKey = apiKey == null ? Patch<string>.Absent : Patch<string>.SetTo(apiKey),
			Provider = provider == null ? Patch<AIPresetProvider>.Absent : Patch<AIPresetProvider>.SetTo(provider.Value),
		};
	}

	[Fact]
	public void A04_RemoveDefaultWithSuccessor_PromotesNextToDefault() {
		Seed("first", "second");

		Assert.True(AIPresetStore.RemovePatch("first", null, out var result), result.Error);

		Assert.Equal("second", AIPresetStore.DefaultPreset?.Name);
		Assert.Equal(["second"], AIPresetStore.Presets.Select(preset => preset.Name));
		var reloaded = JsonHelper.Load<AIPresetDocument>(_path);
		Assert.Equal(["second"], reloaded.Presets.Select(preset => preset.Name));
	}

	[Fact]
	public void A04_RemoveLastDefaultConsumerEntry_IsRejected() {
		Seed("first");
		AIPresetUsageRegistry.LoadForTest([new AIPresetUsageEntry("Mod", "Caller#Go", AIPresetDependency.Default)]);

		Assert.False(AIPresetStore.RemovePatch("first", null, out var result));

		Assert.Single(AIPresetStore.Presets);
		Assert.NotNull(AIPresetStore.Find("first"));
	}

	[Fact]
	public void A04_RemoveLastEntryWithoutDefaultConsumer_IsAllowed() {
		Seed("first");

		Assert.True(AIPresetStore.RemovePatch("first", null, out var result), result.Error);

		Assert.Empty(AIPresetStore.Presets);
		Assert.Null(AIPresetStore.DefaultPreset);
	}

	[Fact]
	public void A05_NamedConsumer_BlocksRenameAndAllowsModelChange() {
		Seed("prod");
		AIPresetUsageRegistry.LoadForTest([new AIPresetUsageEntry("Mod", "Caller#Go", AIPresetDependency.Named("prod"))]);

		Assert.False(AIPresetStore.Apply("prod", new AIPresetPatch { Name = Patch<string>.SetTo("renamed") }, null, out var renameResult));
		Assert.Contains(L.Prefix("AIPresetInUse"), renameResult.Error);
		Assert.Contains("Caller#Go", renameResult.Error);
		Assert.NotNull(AIPresetStore.Find("prod"));

		Assert.True(AIPresetStore.Apply("prod", Patch("m-new"), null, out var modelResult), modelResult.Error);
		Assert.Equal("m-new", AIPresetStore.Find("prod")?.Model);
	}

	[Fact]
	public void A05_ApiKeyChangeWhileReferenced_IsAllowed() {
		Seed("prod");
		AIPresetUsageRegistry.LoadForTest([new AIPresetUsageEntry("Mod", "Caller#Go", AIPresetDependency.Named("prod"))]);

		Assert.True(AIPresetStore.Apply("prod", Patch(apiKey: "k2"), null, out var result), result.Error);

		Assert.Equal("k2", AIPresetStore.Find("prod")?.ApiKey);
	}

	[Fact]
	public async Task A09_ConcurrentDifferentFieldEdits_BothSurvive() {
		Seed("prod");
		var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var failures = 0;
		var workers = new[] {
			Task.Run(() => {
				start.Task.Wait();
				if (!AIPresetStore.Apply("prod", new AIPresetPatch { Model = Patch<string>.SetTo("model-x") }, null, out _)) Interlocked.Increment(ref failures);
			}),
			Task.Run(() => {
				start.Task.Wait();
				if (!AIPresetStore.Apply("prod", new AIPresetPatch { ApiKey = Patch<string>.SetTo("key-y") }, null, out _)) Interlocked.Increment(ref failures);
			}),
		};
		start.SetResult();
		await Task.WhenAll(workers);

		Assert.Equal(0, failures);
		var preset = AIPresetStore.Find("prod")!;
		Assert.Equal("model-x", preset.Model);
		Assert.Equal("key-y", preset.ApiKey);
		Assert.Equal("https://api.example.com/v1", preset.BaseUrl);
		var reloaded = JsonHelper.Load<AIPresetDocument>(_path);
		Assert.Equal("model-x", reloaded.Presets[0].Model);
		Assert.Equal("key-y", reloaded.Presets[0].ApiKey);
	}

	[Fact]
	public void A10_SaveFailure_KeepsDocumentVersionAndPath() {
		Seed("prod");
		var before = AIPresetStore.Snapshot;
		var pathBefore = AIPresetStore.CurrentPath;
		using var locked = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);

		Assert.False(AIPresetStore.Apply("prod", Patch("m2"), null, out var result));

		Assert.False(result.Success);
		Assert.Equal(before.Version, AIPresetStore.Snapshot.Version);
		Assert.Equal("m", AIPresetStore.Find("prod")?.Model);
		Assert.Equal(pathBefore, AIPresetStore.CurrentPath);
	}

	[Fact]
	public void A11_InvalidDocumentFromAnotherPath_KeepsDocumentAndSavePath() {
		Seed("prod");
		var pathBefore = AIPresetStore.CurrentPath;
		var versionBefore = AIPresetStore.Snapshot.Version;
		var other = Path.Combine(_directory, "other.json");
		File.WriteAllText(other, """
			{ "MaxParallel": 5, "Presets": [{ "Name": "", "Provider": "openai", "ApiKey": "k", "BaseUrl": "https://a.example.com", "Model": "m" }] }
			""");

		Assert.False(AIPresetStore.TryLoad(other, out _));

		Assert.Equal(pathBefore, AIPresetStore.CurrentPath);
		Assert.Equal(versionBefore, AIPresetStore.Snapshot.Version);

		Assert.True(AIPresetStore.Apply("prod", Patch("m-after"), null, out var result), result.Error);
		Assert.Equal("m-after", JsonHelper.Load<AIPresetDocument>(_path).Presets[0].Model);
		Assert.Contains("m-after", File.ReadAllText(_path));
	}

	[Theory]
	[InlineData("null")]
	[InlineData("""{ "MaxParallel": null, "Presets": [] }""")]
	[InlineData("""{ "MaxParallel": 5, "Presets": null }""")]
	[InlineData("""{ "MaxParallel": 5, "Presets": "x" }""")]
	public void A12_NullRootNullMaxParallelAndNullPresets_AreRejected(string json) {
		AIPresetStore.ResetForTest();
		File.WriteAllText(_path, json);

		Assert.False(AIPresetStore.TryLoad(_path, out var errors));

		Assert.NotEmpty(errors);
		Assert.Empty(AIPresetStore.Presets);
	}

	[Theory]
	[InlineData("""{ "MaxParallel": 5, "Presets": [null] }""")]
	[InlineData("""{ "MaxParallel": 5, "Presets": [{ "Name": null, "Provider": "openai" }] }""")]
	[InlineData("""{ "MaxParallel": 5, "Presets": [{ "Name": "a", "Provider": null }] }""")]
	[InlineData("""{ "MaxParallel": 5, "Presets": [{ "Name": "a", "Provider": "nope" }] }""")]
	[InlineData("""{ "MaxParallel": 5, "Presets": [{ "Name": "a", "Provider": "openai", "ApiKey": "k", "BaseUrl": "https://a.example.com", "Model": "m", "Temperature": "hot" }] }""")]
	[InlineData("""{ "MaxParallel": 5, "Presets": [{ "Name": "a", "Provider": "openai", "ApiKey": "k", "BaseUrl": "https://a.example.com", "Model": "m", "MaxOutputTokens": 1.5 }] }""")]
	public void A12_MalformedJson_ProducesControlledFailure(string json) {
		AIPresetStore.ResetForTest();
		File.WriteAllText(_path, json);

		Assert.False(AIPresetStore.TryLoad(_path, out var errors));

		Assert.NotEmpty(errors);
		Assert.Empty(AIPresetStore.Presets);
	}

	[Theory]
	[InlineData("""{ "MaxParallel": 5, "Presets": [{ "Name": " a", "Provider": "openai", "ApiKey": "k", "BaseUrl": "https://a.example.com", "Model": "m" }] }""")]
	[InlineData("""{ "MaxParallel": 5, "Presets": [{ "Name": "a ", "Provider": "openai", "ApiKey": "k", "BaseUrl": "https://a.example.com", "Model": "m" }] }""")]
	[InlineData("""{ "MaxParallel": 5, "Presets": [{ "Name": "a\nb", "Provider": "openai", "ApiKey": "k", "BaseUrl": "https://a.example.com", "Model": "m" }] }""")]
	public void A12_IllegalNames_AreRejectedWithoutTrimming(string json) {
		AIPresetStore.ResetForTest();
		File.WriteAllText(_path, json);

		Assert.False(AIPresetStore.TryLoad(_path, out var errors));

		Assert.NotEmpty(errors);
		Assert.Empty(AIPresetStore.Presets);
	}

	[Fact]
	public void A12_NonFiniteSampling_IsRejected() {
		File.WriteAllText(_path, """
			{ "MaxParallel": 5, "Presets": [{ "Name": "a", "Provider": "openai", "ApiKey": "k", "BaseUrl": "https://a.example.com", "Model": "m", "Temperature": 1e400 }] }
			""");

		Assert.False(AIPresetStore.TryLoad(_path, out var errors));

		Assert.NotEmpty(errors);
	}

	[Fact]
	public void A12_NumericProviderValue_DoesNotBypassTheChoiceRange() {
		File.WriteAllText(_path, """
			{ "MaxParallel": 5, "Presets": [{ "Name": "a", "Provider": 99, "ApiKey": "k", "BaseUrl": "https://a.example.com", "Model": "m" }] }
			""");

		Assert.False(AIPresetStore.TryLoad(_path, out _));
	}

	[Fact]
	public void A13_SamplingSetAndUnset_RoundTrip() {
		Seed("prod");

		Assert.True(AIPresetStore.Apply("prod", new AIPresetPatch { Temperature = Patch<float>.SetTo(0.7f), TopP = Patch<float>.SetTo(0.9f) }, null, out var setResult), setResult.Error);
		var set = AIPresetStore.Find("prod")!;
		Assert.Equal(0.7f, set.Temperature);
		Assert.Equal(0.9f, set.TopP);

		Assert.True(AIPresetStore.Apply("prod", new AIPresetPatch { Temperature = Patch<float>.Clear }, null, out var clearResult), clearResult.Error);
		var cleared = AIPresetStore.Find("prod")!;
		Assert.Null(cleared.Temperature);
		Assert.Equal(0.9f, cleared.TopP);
	}

	[Fact]
	public void A13_ProviderAtomicSwitch_InOneCommit() {
		Seed("prod");

		var patch = new AIPresetPatch {
			Provider = Patch<AIPresetProvider>.SetTo(AIPresetProvider.Vertex),
			Project = Patch<string>.SetTo("p"),
			Location = Patch<string>.SetTo("us-central1"),
			ApiKey = Patch<string>.Clear,
			BaseUrl = Patch<string>.Clear,
		};

		Assert.True(AIPresetStore.Apply("prod", patch, null, out var result), result.Error);

		var preset = AIPresetStore.Find("prod")!;
		Assert.Equal(AIPresetProvider.Vertex, preset.Provider);
		Assert.Equal("p", preset.Project);
		Assert.Equal("us-central1", preset.Location);
		Assert.Empty(preset.ApiKey);
	}

	[Fact]
	public void A23_StaleExpectedVersion_IsRejected() {
		Seed("prod");
		var version = AIPresetStore.Snapshot.Version;
		Assert.True(AIPresetStore.Apply("prod", Patch("m2"), null, out _));

		Assert.False(AIPresetStore.Apply("prod", Patch("m3"), version, out var result));

		Assert.Contains(L.Prefix("AIPresetStaleVersion"), result.Error);
		Assert.Equal("m2", AIPresetStore.Find("prod")?.Model);
	}

	[Fact]
	public void A23_ValidationFailureWithBraces_RemainsRenderedText() {
		Seed("prod", "other{0}");

		Assert.False(AIPresetStore.Apply("prod", new AIPresetPatch { Name = Patch<string>.SetTo("other{0}") }, null, out var result));

		Assert.Contains("other{0}", result.Error);
		Assert.Contains(L.Prefix("AIPresetDuplicateName"), result.Error);
		Assert.Equal("prod", AIPresetStore.DefaultPreset?.Name);
	}

	[Fact]
	public void A24_PublishedSnapshotIsImmutable() {
		Seed("prod", "dev");

		var snapshot = AIPresetStore.Snapshot;
		var before = snapshot.Presets.Count;
		Assert.True(AIPresetStore.Apply("prod", Patch("m2"), null, out _));

		Assert.Equal(before, snapshot.Presets.Count);
		Assert.Equal("m", snapshot.Presets[0].Model);
		Assert.Equal("m2", AIPresetStore.Snapshot.Presets[0].Model);
		Assert.IsAssignableFrom<IReadOnlyList<AIPreset>>(snapshot.Presets);
	}

	[Fact]
	public void NameSpacesInTheMiddle_ArePreservedAndAddressable() {
		Seed("my preset");
		AIPresetUsageRegistry.LoadForTest([new AIPresetUsageEntry("Mod", "Caller#Go", AIPresetDependency.Named("my preset"))]);

		Assert.Equal("my preset", AIPresetStore.Find("my preset")?.Name);
		Assert.True(AIPresetStore.Apply("my preset", Patch(apiKey: "k2"), null, out var result), result.Error);
		Assert.Equal("k2", AIPresetStore.Find("my preset")?.ApiKey);
	}
}
