using Pengin1011;
using Pengin1011.Core;
using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class AISecretMaskingTests : IDisposable {
	private const string FakeSecret = "sk-fake-secret-value-0001";
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"qq1011_secret_{Guid.NewGuid():N}");
	private readonly string _path;

	public AISecretMaskingTests() {
		Directory.CreateDirectory(_directory);
		_path = Path.Combine(_directory, "aipresets.json");
		AIPresetUsageRegistry.ForgetAll();
	}

	public void Dispose() {
		AIPresetStore.ResetForTest();
		AIPresetUsageRegistry.ForgetAll();
		try { Directory.Delete(_directory, true); } catch (IOException) { }
	}

	private void Seed() {
		File.WriteAllText(_path, $$"""
			{ "MaxParallel": 5, "Presets": [{ "Name": "prod", "Provider": "openai", "ApiKey": "{{FakeSecret}}", "BaseUrl": "https://api.example.com/v1", "Model": "m" }] }
			""");
		Assert.True(AIPresetStore.TryLoad(_path, out _));
	}

	[Fact]
	public async Task A22_CliShow_DoesNotRevealTheApiKey() {
		Seed();

		var result = await CliDispatcher.ExecuteAsync("preset show prod");

		Assert.DoesNotContain(FakeSecret, result.Output);
		Assert.Contains(Secret.Mask, result.Output);
		Assert.Contains("apikey", result.Output);
	}

	[Fact]
	public void A22_ConnectionKeyDiagnostics_NeverContainTheApiKey() {
		Seed();

		var describe = AIPresetStore.DefaultPreset!.Connection.Describe();

		Assert.DoesNotContain(FakeSecret, describe);
		Assert.Contains("OpenAI", describe);
		Assert.Contains("https://api.example.com/v1", describe);
	}

	[Fact]
	public async Task A22_CliShow_UnconfiguredKey_ReportsNotConfigured() {
		File.WriteAllText(_path, """
			{ "MaxParallel": 5, "Presets": [{ "Name": "adc", "Provider": "vertex", "Project": "p", "Location": "us-central1", "Model": "m" }] }
			""");
		Assert.True(AIPresetStore.TryLoad(_path, out _));

		var result = await CliDispatcher.ExecuteAsync("preset show adc");

		Assert.Contains(L.Get("SecretNotConfigured"), result.Output);
	}

	[Fact]
	public async Task A22_MaskedKey_StillReportsConfigured() {
		Seed();

		var result = await CliDispatcher.ExecuteAsync("preset show prod");

		Assert.DoesNotContain(L.Get("SecretNotConfigured"), result.Output);
	}
}
