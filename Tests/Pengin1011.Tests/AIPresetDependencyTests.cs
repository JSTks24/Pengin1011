using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

public sealed class AIPresetDependencyTests {
	[Fact]
	public void Default_MatchesAnyNonEmptyPresetList() {
		Assert.True(AIPresetDependency.Default.Matches([Preset("prod")]));
		Assert.False(AIPresetDependency.Default.Matches([]));
	}

	[Fact]
	public void Named_MatchesOnlyTheExactName() {
		Assert.True(AIPresetDependency.Named("prod").Matches([Preset("prod")]));
		Assert.False(AIPresetDependency.Named("Prod").Matches([Preset("prod")]));
		Assert.False(AIPresetDependency.Named("absent").Matches([Preset("prod")]));
	}

	[Fact]
	public void LiteralDefaultMarkerName_IsAnOrdinaryNamed() {
		var dependency = AIPresetDependency.Named("<default>");

		Assert.False(dependency.IsDefault);
		Assert.False(dependency.Matches([Preset("prod")]));
		Assert.True(dependency.Matches([Preset("<default>")]));
	}

	[Fact]
	public void NamedEmptyString_CollapsesToDefault() {
		Assert.True(AIPresetDependency.Named("").IsDefault);
		Assert.True(AIPresetDependency.Named(null!).IsDefault);
	}
	[Fact]
	public void Equality_SeparatesDefaultFromLiteralMarker() {
		Assert.NotEqual(AIPresetDependency.Default, AIPresetDependency.Named("<default>"));
		Assert.Equal(AIPresetDependency.Named("prod"), AIPresetDependency.Named("prod"));
		Assert.NotEqual(AIPresetDependency.Named("prod"), AIPresetDependency.Named("Prod"));
	}

	private static AIPreset Preset(string name) {
		return new AIPreset { Name = name, Provider = AIPresetProvider.OpenAI, ApiKey = "k", BaseUrl = "https://api.example.com/v1", Model = "m" };
	}
}
