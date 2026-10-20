using System.ClientModel.Primitives;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Google.GenAI.Types;
using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

public sealed class AIBackendHttpTests {
	[Fact]
	public async Task OpenAIBackend_Preset_SendsToBaseUrlWithKeyAndSampling() {
		var handler = new RecordingHandler(OpenAICompletionJson);
		var backend = new OpenAIBackend(OpenAIPreset("override-key"), new HttpClientPipelineTransport(new HttpClient(handler)));
		var request = new AIRequest {
			Messages = [new AIMessage(AIRole.User, "hi")],
			Model = "override-model",
			Temperature = 0.5f,
			TopP = 0.9f,
			MaxOutputTokens = 50,
		};

		var result = await backend.CompleteAsync(request, CancellationToken.None);

		Assert.NotNull(result);
		Assert.Equal("hello", result!.Text);
		Assert.Equal(3, result.Usage.InputTokens);
		Assert.Equal(4, result.Usage.OutputTokens);
		var sent = Assert.Single(handler.Requests);
		Assert.Equal("https://api.example.com/v1/chat/completions", sent.RequestUri!.GetLeftPart(UriPartial.Path));
		Assert.Equal("Bearer", sent.Headers.Authorization?.Scheme);
		Assert.Equal("override-key", sent.Headers.Authorization?.Parameter);
		var body = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement;
		Assert.Equal("override-model", body.GetProperty("model").GetString());
		Assert.Equal(50, body.GetProperty("max_completion_tokens").GetInt32());
		Assert.Equal(0.5f, body.GetProperty("temperature").GetSingle());
		Assert.Equal(0.9f, body.GetProperty("top_p").GetSingle());
	}

	[Fact]
	public async Task OpenAIBackend_WithoutSampling_OmitsFields() {
		var handler = new RecordingHandler(OpenAICompletionJson);
		var backend = new OpenAIBackend(OpenAIPreset(), new HttpClientPipelineTransport(new HttpClient(handler)));

		await backend.CompleteAsync(MakeRequest("m"), CancellationToken.None);

		var body = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement;
		Assert.False(body.TryGetProperty("max_completion_tokens", out _));
		Assert.False(body.TryGetProperty("temperature", out _));
		Assert.False(body.TryGetProperty("top_p", out _));
	}

	[Fact]
	public async Task OpenAIBackend_PresetModel_UsedWhenRequestOmitsModel() {
		var handler = new RecordingHandler(OpenAICompletionJson);
		var backend = new OpenAIBackend(OpenAIPreset(), new HttpClientPipelineTransport(new HttpClient(handler)));

		await backend.CompleteAsync(MakeRequest(null), CancellationToken.None);

		var body = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement;
		Assert.Equal("preset-model", body.GetProperty("model").GetString());
	}

	[Fact]
	public async Task OpenAIBackend_Stream_Sampling_SentInBody() {
		var handler = new RecordingHandler(OpenAIStreamSse, "text/event-stream");
		var backend = new OpenAIBackend(OpenAIPreset(), new HttpClientPipelineTransport(new HttpClient(handler)));
		var request = new AIRequest {
			Messages = [new AIMessage(AIRole.User, "hi")],
			Model = "override-model",
			Temperature = 0.5f,
			TopP = 0.9f,
			MaxOutputTokens = 50,
		};

		var text = new StringBuilder();
		await foreach (var delta in backend.StreamAsync(request, CancellationToken.None)) {
			text.Append(delta.Text);
		}

		Assert.Equal("hello", text.ToString());
		var body = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement;
		Assert.Equal(0.5f, body.GetProperty("temperature").GetSingle());
		Assert.Equal(0.9f, body.GetProperty("top_p").GetSingle());
		Assert.Equal(50, body.GetProperty("max_completion_tokens").GetInt32());
		Assert.True(body.GetProperty("stream").GetBoolean());
	}

	[Fact]
	public async Task GeminiBackend_Preset_SendsSamplingAndSystem() {
		var handler = new RecordingHandler(GeminiResponseJson);
		var backend = new GeminiBackend(GeminiPreset("g-key"), new ClientOptions { HttpClientFactory = () => new HttpClient(handler) });
		var request = new AIRequest {
			System = "be brief",
			Messages = [new AIMessage(AIRole.User, "hi")],
			Model = "gemini-2.0-flash",
			Temperature = 0.5f,
			TopP = 0.9f,
			MaxOutputTokens = 100,
		};

		var result = await backend.CompleteAsync(request, CancellationToken.None);

		Assert.NotNull(result);
		Assert.Equal("hello", result!.Text);
		Assert.Equal(3, result.Usage.InputTokens);
		Assert.Equal(4, result.Usage.OutputTokens);
		var sent = Assert.Single(handler.Requests);
		Assert.Equal("generativelanguage.googleapis.com", sent.RequestUri!.Host);
		Assert.Contains("gemini-2.0-flash:generateContent", sent.RequestUri!.ToString());
		Assert.Equal("g-key", Assert.Single(sent.Headers.GetValues("x-goog-api-key")));
		var body = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement;
		var config = body.GetProperty("generationConfig");
		Assert.Equal(0.5f, config.GetProperty("temperature").GetSingle());
		Assert.Equal(0.9f, config.GetProperty("topP").GetSingle());
		Assert.Equal(100, config.GetProperty("maxOutputTokens").GetInt32());
		Assert.Equal("be brief", body.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
	}

	[Fact]
	public async Task GeminiBackend_Preset_OfficialBaseUrl() {
		var handler = new RecordingHandler(GeminiResponseJson);
		var preset = new AIPreset { Name = "g", Provider = AIPresetProvider.Gemini, ApiKey = "g-key", BaseUrl = "https://generativelanguage.googleapis.com:443/", Model = "m" };
		var backend = new GeminiBackend(preset, new ClientOptions { HttpClientFactory = () => new HttpClient(handler) });

		await backend.CompleteAsync(MakeRequest("m"), CancellationToken.None);

		var sent = Assert.Single(handler.Requests);
		Assert.Equal("generativelanguage.googleapis.com", sent.RequestUri!.Host);
	}

	[Fact]
	public async Task Shutdown_DisposesPresetBackend() {
		var disposable = new DisposableBackend();
		AIPresetStore.LoadForTest(new AIPresetDocument { Presets = [OpenAIPreset()] });
		AIClient.Init(2, _ => disposable);
		await AIClient.CompleteAsync(MakeRequest(null));

		AIClient.Shutdown();

		Assert.True(disposable.Disposed);
	}

	[Fact]
	public async Task Validate_PresetAndRequestBothMissingModel_Throws() {
		var preset = new AIPreset { Name = "m", Provider = AIPresetProvider.Gemini, ApiKey = "k", Model = "" };
		AIPresetStore.LoadForTest(new AIPresetDocument { Presets = [preset] });
		AIClient.Init(2, _ => new CountingBackend());

		await Assert.ThrowsAsync<ArgumentException>(() => AIClient.CompleteAsync(MakeRequest(null)));
	}

	[Fact]
	public async Task Validate_ZeroMaxOutputTokens_Throws() {
		AIPresetStore.LoadForTest(new AIPresetDocument { Presets = [OpenAIPreset()] });
		AIClient.Init(2, _ => new CountingBackend());
		var request = new AIRequest {
			Messages = [new AIMessage(AIRole.User, "hi")],
			Model = "m",
			MaxOutputTokens = 0,
		};

		await Assert.ThrowsAsync<ArgumentException>(() => AIClient.CompleteAsync(request));
	}

	[Fact]
	public async Task Validate_NegativeTemperature_Throws() {
		AIPresetStore.LoadForTest(new AIPresetDocument { Presets = [OpenAIPreset()] });
		AIClient.Init(2, _ => new CountingBackend());
		var request = new AIRequest {
			Messages = [new AIMessage(AIRole.User, "hi")],
			Model = "m",
			Temperature = -0.5f,
		};

		await Assert.ThrowsAsync<ArgumentException>(() => AIClient.CompleteAsync(request));
	}

	private const string OpenAICompletionJson = """
		{"id":"chatcmpl-1","object":"chat.completion","created":1700000000,"model":"override-model","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":4,"total_tokens":7}}
		""";

	private const string OpenAIStreamSse = """
		data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1700000000,"model":"override-model","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}

		data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1700000000,"model":"override-model","choices":[{"index":0,"delta":{"content":"he"},"finish_reason":null}]}

		data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1700000000,"model":"override-model","choices":[{"index":0,"delta":{"content":"llo"},"finish_reason":null}]}

		data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1700000000,"model":"override-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":4,"total_tokens":7}}

		data: [DONE]

		""";

	private const string GeminiResponseJson = """
		{"candidates":[{"content":{"parts":[{"text":"hello"}],"role":"model"}}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":4,"totalTokenCount":7}}
		""";

	private static AIRequest MakeRequest(string? model) {
		return new AIRequest {
			System = "sys",
			Messages = [new AIMessage(AIRole.User, "hi")],
			Model = model,
		};
	}

	[Fact]
	public async Task PresetStoreApply_DisposesEvictedBackend() {
		var directory = Path.Combine(Path.GetTempPath(), $"qq1011_evict_{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, "aipresets.json");
		System.IO.File.WriteAllText(path, """
			{ "MaxParallel": 5, "Presets": [{ "Name": "p", "Provider": "openai", "ApiKey": "key-a", "BaseUrl": "https://api.example.com/v1", "Model": "preset-model" }] }
			""");
		try {
			Assert.True(AIPresetStore.TryLoad(path, out _));
			var first = new DisposableBackend();
			AIClient.Init(2, _ => first);
			await AIClient.CompleteAsync(MakeRequest(null));
			Assert.False(first.Disposed);

			Assert.True(AIPresetStore.Apply("p", new AIPresetPatch { ApiKey = Patch<string>.SetTo("key-b") }, null, out var result), result.Error);

			Assert.True(first.Disposed);
		} finally {
			AIPresetStore.ResetForTest();
			try { Directory.Delete(directory, true); } catch (IOException) { }
		}
	}

	[Fact]
	public async Task InvalidateBackendCache_DisposesTheEvictedInstance() {
		var disposable = new DisposableBackend();
		AIPresetStore.LoadForTest(new AIPresetDocument { Presets = [OpenAIPreset()] });
		AIClient.Init(2, _ => disposable);
		await AIClient.CompleteAsync(MakeRequest(null));

		AIClient.InvalidateBackendCache();

		Assert.True(disposable.Disposed);
	}

	[Fact]
	public async Task PresetConnectionParamsChanged_CreatesFreshBackend() {
		var created = 0;
		AIPresetStore.LoadForTest(new AIPresetDocument { Presets = [OpenAIPreset("key-a")] });
		AIClient.Init(2, _ => {
			created++;
			return new CountingBackend();
		});
		await AIClient.CompleteAsync(MakeRequest(null));
		Assert.Equal(1, created);

		AIPresetStore.LoadForTest(new AIPresetDocument { Presets = [OpenAIPreset("key-b")] });
		await AIClient.CompleteAsync(MakeRequest(null));

		Assert.Equal(2, created);
	}

	[Fact]
	public async Task PresetConnectionParamsUnchanged_ReusesBackend() {
		var created = 0;
		AIPresetStore.LoadForTest(new AIPresetDocument { Presets = [OpenAIPreset("key-a")] });
		AIClient.Init(2, _ => {
			created++;
			return new CountingBackend();
		});

		await AIClient.CompleteAsync(MakeRequest(null));
		await AIClient.CompleteAsync(MakeRequest(null));

		Assert.Equal(1, created);
	}

	private static AIPreset OpenAIPreset(string apiKey = "k") {
		return new AIPreset { Name = "p", Provider = AIPresetProvider.OpenAI, ApiKey = apiKey, BaseUrl = "https://api.example.com/v1", Model = "preset-model" };
	}

	private static AIPreset GeminiPreset(string apiKey) {
		return new AIPreset { Name = "g", Provider = AIPresetProvider.Gemini, ApiKey = apiKey, Model = "preset-model" };
	}

	private class CountingBackend : IAIBackend {
		public int Calls;

		public Task<AIResult?> CompleteAsync(AIRequest request, CancellationToken ct) {
			Calls++;
			return Task.FromResult<AIResult?>(new AIResult("over", new AIUsage(1, 2)));
		}

		public async IAsyncEnumerable<AIStreamDelta> StreamAsync(AIRequest request, [EnumeratorCancellation] CancellationToken ct) {
			Calls++;
			await Task.Yield();
			yield return new AIStreamDelta("over");
		}
	}

	private sealed class DisposableBackend : CountingBackend, IDisposable {
		public bool Disposed;

		public void Dispose() {
			Disposed = true;
		}
	}

	private sealed class RecordingHandler(string responseJson, string contentType = "application/json") : HttpMessageHandler {
		public readonly List<HttpRequestMessage> Requests = [];
		public readonly List<string> Bodies = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
			Requests.Add(request);
			Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
			return new HttpResponseMessage(HttpStatusCode.OK) {
				Content = new StringContent(responseJson, Encoding.UTF8, contentType),
			};
		}
	}
}
