using Pengin1011.Core.Logging;
using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

public sealed class AIPresetValidatorTests {
	private sealed class FakeSource;

	private static AIPreset Preset(string name) {
		return new AIPreset {
			Name = name,
			Provider = AIPresetProvider.OpenAI,
			ApiKey = "k",
			BaseUrl = "https://api.example.com/v1",
			Model = "m",
		};
	}

	private static AIPresetUsageEntry Named(string owner, string callSite, string presetName) {
		return new AIPresetUsageEntry(owner, callSite, AIPresetDependency.Named(presetName));
	}

	private static AIPresetUsageEntry Default(string owner, string callSite) {
		return new AIPresetUsageEntry(owner, callSite, AIPresetDependency.Default);
	}

	private static string TempErrorLog() {
		return Path.Combine(Path.GetTempPath(), $"qq1011_validator_{Guid.NewGuid():N}", "error.log");
	}

	[Fact]
	public void CollectMissing_AllPresent_ReturnsEmpty() {
		var failures = AIPresetValidator.CollectMissing([Preset("prod")], [Named("FakeModule", "Caller#Go", "prod")]);

		Assert.Empty(failures);
	}

	[Fact]
	public void CollectMissing_MissingPreset_NamesOwnerCallSiteAndPreset() {
		var failures = AIPresetValidator.CollectMissing([Preset("prod")], [Named("FakeModule", "Caller#Go", "absent")]);

		var failure = Assert.Single(failures);
		Assert.Contains("FakeModule", failure);
		Assert.Contains("Caller#Go", failure);
		Assert.Contains("absent", failure);
	}

	[Fact]
	public void CollectMissing_CollectsEveryFailureNotJustTheFirst() {
		var failures = AIPresetValidator.CollectMissing(
			[Preset("prod")],
			[
				Named("ModuleA", "A#One", "absent-a"),
				Named("ModuleB", "B#Two", "absent-b"),
				Named("ModuleC", "C#Three", "prod"),
				Named("ModuleD", "D#Four", "absent-d"),
			]);

		Assert.Equal(3, failures.Count);
		Assert.Contains(failures, failure => failure.Contains("absent-a"));
		Assert.Contains(failures, failure => failure.Contains("absent-b"));
		Assert.Contains(failures, failure => failure.Contains("absent-d"));
	}

	[Fact]
	public void CollectMissing_DefaultDependencyWithNoPresets_Fails() {
		var failures = AIPresetValidator.CollectMissing([], [Default("FakeModule", "Caller#Go")]);

		var failure = Assert.Single(failures);
		Assert.Contains("FakeModule", failure);
		Assert.Contains(L.Prefix("AIPresetUsageDefaultMissing"), failure);
	}

	[Fact]
	public void CollectMissing_DefaultDependencyWithPresets_Passes() {
		var failures = AIPresetValidator.CollectMissing([Preset("prod")], [Default("FakeModule", "Caller#Go")]);

		Assert.Empty(failures);
	}

	[Fact]
	public void CollectMissing_PresetLiterallyNamedDefault_IsOrdinaryNamed() {
		var failures = AIPresetValidator.CollectMissing([Preset("prod")], [Named("FakeModule", "Caller#Go", "<default>")]);

		Assert.Single(failures);
	}

	[Fact]
	public void CollectMissing_PresetLiterallyNamedDefault_MatchesItself() {
		var failures = AIPresetValidator.CollectMissing([Preset("<default>")], [Named("FakeModule", "Caller#Go", "<default>")]);

		Assert.Empty(failures);
	}

	[Fact]
	public void CollectMissing_NoUsages_Passes() {
		Assert.Empty(AIPresetValidator.CollectMissing([], []));
	}

	[Fact]
	public void CollectMissing_IsOrdinal() {
		var failures = AIPresetValidator.CollectMissing([Preset("Prod")], [Named("FakeModule", "Caller#Go", "prod")]);

		Assert.Single(failures);
	}

	[Fact]
	public void TryValidate_LogsEveryFailureAndReturnsFalse() {
		var logPath = TempErrorLog();
		Logger.SetLogPathForTest(logPath);
		try {
			AIPresetUsageRegistry.LoadForTest([
				Named("ModuleA", "A#One", "absent-a"),
				Named("ModuleB", "B#Two", "absent-b"),
			]);
			AIPresetStore.LoadForTest(new AIPresetDocument { Presets = [] });

			Assert.False(AIPresetValidator.TryValidate(out var failures));

			Assert.Equal(2, failures.Count);
			var log = File.ReadAllText(logPath);
			Assert.Contains("absent-a", log);
			Assert.Contains("absent-b", log);
		} finally {
			Logger.SetLogPathForTest(null);
			AIPresetStore.ResetForTest();
			AIPresetUsageRegistry.ForgetAll();
			var directory = Path.GetDirectoryName(logPath)!;
			if (Directory.Exists(directory)) Directory.Delete(directory, true);
		}
	}

	[Fact]
	public void TryValidate_AllSatisfied_WritesNoLogAndReturnsTrue() {
		var logPath = TempErrorLog();
		Logger.SetLogPathForTest(logPath);
		try {
			AIPresetUsageRegistry.LoadForTest([Named("ModuleA", "A#One", "prod")]);
			AIPresetStore.LoadForTest(new AIPresetDocument { Presets = [Preset("prod")] });

			Assert.True(AIPresetValidator.TryValidate(out var failures));

			Assert.Empty(failures);
			Assert.False(File.Exists(logPath));
		} finally {
			Logger.SetLogPathForTest(null);
			AIPresetStore.ResetForTest();
			AIPresetUsageRegistry.ForgetAll();
			var directory = Path.GetDirectoryName(logPath)!;
			if (Directory.Exists(directory)) Directory.Delete(directory, true);
		}
	}
}
