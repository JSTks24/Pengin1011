using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

public sealed class AIPresetPatchTests {
	private static AIPreset Preset() {
		return new AIPreset {
			Name = "prod",
			Provider = AIPresetProvider.OpenAI,
			ApiKey = "k",
			BaseUrl = "https://api.example.com/v1",
			Model = "m1",
			Temperature = 0.5f,
			TopP = 0.9f,
			MaxOutputTokens = 100,
		};
	}

	[Fact]
	public void AbsentPatch_KeepsEveryField() {
		var result = new AIPresetPatch().Apply(Preset());

		Assert.Equal("prod", result.Name);
		Assert.Equal("k", result.ApiKey);
		Assert.Equal("m1", result.Model);
		Assert.Equal(0.5f, result.Temperature);
		Assert.Equal(0.9f, result.TopP);
		Assert.Equal(100, result.MaxOutputTokens);
	}

	[Fact]
	public void SetPatch_ReplacesOnlyTheGivenField() {
		var patch = new AIPresetPatch { Model = Patch<string>.SetTo("m2") };

		var result = patch.Apply(Preset());

		Assert.Equal("m2", result.Model);
		Assert.Equal("k", result.ApiKey);
		Assert.Equal(0.5f, result.Temperature);
	}

	[Fact]
	public void ClearPatch_UnsetsNullableSampling() {
		var patch = new AIPresetPatch {
			Temperature = Patch<float>.Clear,
			TopP = Patch<float>.Clear,
			MaxOutputTokens = Patch<int>.Clear,
		};

		var result = patch.Apply(Preset());

		Assert.Null(result.Temperature);
		Assert.Null(result.TopP);
		Assert.Null(result.MaxOutputTokens);
		Assert.Equal("m1", result.Model);
	}

	[Fact]
	public void ClearAndAbsent_AreDistinguishable() {
		var absent = new AIPresetPatch { Temperature = Patch<float>.Absent };
		var cleared = new AIPresetPatch { Temperature = Patch<float>.Clear };

		Assert.Equal(0.5f, absent.Apply(Preset()).Temperature);
		Assert.Null(cleared.Apply(Preset()).Temperature);
	}

	[Fact]
	public void EmptyPatch_ReportsEmpty() {
		Assert.True(new AIPresetPatch().IsEmpty);
		Assert.False(new AIPresetPatch { Model = Patch<string>.SetTo("x") }.IsEmpty);
	}

	[Fact]
	public void ProviderSwitch_TravelsInOnePatch() {
		var patch = new AIPresetPatch {
			Provider = Patch<AIPresetProvider>.SetTo(AIPresetProvider.Vertex),
			Project = Patch<string>.SetTo("p"),
			Location = Patch<string>.SetTo("us-central1"),
		};

		var result = patch.Apply(Preset());

		Assert.Equal(AIPresetProvider.Vertex, result.Provider);
		Assert.Equal("p", result.Project);
		Assert.Equal("us-central1", result.Location);
	}
}
