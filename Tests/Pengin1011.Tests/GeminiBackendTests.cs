using Google.GenAI;
using Pengin1011.Core;
using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

public sealed class GeminiBackendTests {
	private const string VertexEnv = "GOOGLE_GENAI_USE_VERTEXAI";
	private const string EnterpriseEnv = "GOOGLE_GENAI_USE_ENTERPRISE";

	private static void ClearGenAIEnv() {
		Environment.SetEnvironmentVariable(VertexEnv, null);
		Environment.SetEnvironmentVariable(EnterpriseEnv, null);
	}

	[Fact]
	public void KeyBranch_ExplicitDeveloperMode_WinsOverContradictoryEnv() {
		ClearGenAIEnv();
		Environment.SetEnvironmentVariable(VertexEnv, "true");
		Environment.SetEnvironmentVariable(EnterpriseEnv, "true");
		try {
			var backend = new GeminiBackend(new GeminiConfig { ApiKey = "fake-key", Model = "gemini-fake" });
			Assert.NotNull(backend);
		} finally {
			ClearGenAIEnv();
		}
	}

	[Fact]
	public void VertexBranch_ExplicitEnterpriseMode_PassesModeValidation() {
		ClearGenAIEnv();
		try {
			try {
				var backend = new GeminiBackend(new GeminiConfig { Project = "fake-project", Location = "us-central1", Model = "gemini-fake" });
				Assert.NotNull(backend);
			} catch (Exception e) {
				Assert.False(e.Message.Contains("vertex", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("enterprise", StringComparison.OrdinalIgnoreCase), $"仍处于模式校验失败：{e.Message}");
			}
		} finally {
			ClearGenAIEnv();
		}
	}

	[Fact]
	public void VertexBranch_FakeCredential_ConstructsWithoutModeError() {
		ClearGenAIEnv();
		try {
			var client = new Client(enterprise: true, credential: new FakeCredential(), project: "fake-project", location: "us-central1");
			Assert.NotNull(client);
		} finally {
			ClearGenAIEnv();
		}
	}

	[Fact]
	public void EnterpriseAndVertexConflict_ThrowsArgumentException() {
		ClearGenAIEnv();
		Assert.Throws<ArgumentException>(() => new Client(enterprise: true, vertexAI: false, project: "fake-project", location: "us-central1"));
	}

	[Fact]
	public void VertexBranch_WithoutModeSelection_Throws() {
		ClearGenAIEnv();
		Assert.Throws<ArgumentException>(() => new Client(project: "fake-project", location: "us-central1"));
	}
}

file sealed class FakeCredential : Google.Apis.Auth.OAuth2.ICredential {
	public void Initialize(Google.Apis.Http.ConfigurableHttpClient httpClient) {
	}

	public Task InterceptAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) {
		return Task.CompletedTask;
	}

	public Task<bool> HandleResponseAsync(System.Net.Http.HttpResponseMessage response, bool retrySupported, CancellationToken cancellationToken) {
		return Task.FromResult(false);
	}

	public Task<string> GetAccessTokenForRequestAsync(string? authUri = null, CancellationToken cancellationToken = default) {
		return Task.FromResult("fake-access-token");
	}
}
