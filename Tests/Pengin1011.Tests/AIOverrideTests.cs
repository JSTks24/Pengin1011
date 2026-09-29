using System.ClientModel.Primitives;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Google.GenAI.Types;
using Pengin1011.Core;
using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

public sealed class AIOverrideTests {
	[Fact]
	public async Task Complete_EndpointOverride_RoutesToFactoryBackend() {
		var openAI = new CountingBackend();
		var over = new CountingBackend();
		AIClient.Init(openAI, null, AIProvider.OpenAI, 2, (_, _) => over);

		var result = await AIClient.CompleteAsync(MakeRequest(OpenAIEndpoint(), "m"));

		Assert.NotNull(result);
		Assert.Equal("over", result!.Text);
		Assert.Equal(1, over.Calls);
		Assert.Equal(0, openAI.Calls);
	}

	[Fact]
	public async Task Complete_EndpointOverride_WorksWhenStackUnconfigured() {
		var over = new CountingBackend();
		AIClient.Init(null, null, AIProvider.OpenAI, 2, (_, _) => over);

		var result = await AIClient.CompleteAsync(AIProvider.Gemini, MakeRequest(GeminiKeyEndpoint(), "m"));

		Assert.NotNull(result);
		Assert.Equal(1, over.Calls);
	}

	[Fact]
	public async Task Complete_EndpointOverride_ReusesCachedBackend() {
		var created = 0;
		CountingBackend? first = null;
		AIClient.Init(null, null, AIProvider.OpenAI, 2, (_, _) => {
			created++;
			first = new CountingBackend();
			return first;
		});
		var endpoint = OpenAIEndpoint();

		await AIClient.CompleteAsync(MakeRequest(endpoint, "m"));
		await AIClient.CompleteAsync(MakeRequest(endpoint, "m"));

		Assert.Equal(1, created);
		Assert.Equal(2, first!.Calls);
	}

	[Fact]
	public async Task Complete_DifferentEndpoints_UseSeparateBackends() {
		var created = 0;
		AIClient.Init(null, null, AIProvider.OpenAI, 2, (_, _) => {
			created++;
			return new CountingBackend();
		});

		await AIClient.CompleteAsync(MakeRequest(OpenAIEndpoint("k1"), "m"));
		await AIClient.CompleteAsync(MakeRequest(OpenAIEndpoint("k2"), "m"));

		Assert.Equal(2, created);
	}

	[Fact]
	public async Task Stream_EndpointOverride_RoutesToFactoryBackend() {
		var over = new CountingBackend();
		AIClient.Init(null, null, AIProvider.OpenAI, 2, (_, _) => over);

		var deltas = new List<AIStreamDelta>();
		await foreach (var delta in AIClient.StreamAsync(MakeRequest(OpenAIEndpoint(), "m"))) {
			deltas.Add(delta);
		}

		Assert.Equal(1, over.Calls);
		Assert.Equal("over", Assert.Single(deltas).Text);
	}

	[Fact]
	public void Stream_InvalidRequest_ThrowsWithoutEnumerating() {
		AIClient.Init(null, null, AIProvider.OpenAI, 2);

		Assert.Throws<ArgumentException>(() => AIClient.StreamAsync(new AIRequest {
			Messages = [new AIMessage(AIRole.User, "hi")],
			Model = "m",
			MaxOutputTokens = 0,
		}));
	}

	[Fact]
	public async Task Shutdown_DisposesOverrideBackend() {
		var over = new DisposableBackend();
		AIClient.Init(null, null, AIProvider.OpenAI, 2, (_, _) => over);
		await AIClient.CompleteAsync(MakeRequest(OpenAIEndpoint(), "m"));

		AIClient.Shutdown();

		Assert.True(over.Disposed);
	}

	[Fact]
	public async Task Init_ResetsOverrideCache() {
		var created = 0;
		CountingBackend Factory(AIProvider stack, AIEndpoint endpoint) {
			created++;
			return new CountingBackend();
		}
		AIClient.Init(null, null, AIProvider.OpenAI, 2, Factory);
		await AIClient.CompleteAsync(MakeRequest(OpenAIEndpoint(), "m"));

		AIClient.Init(null, null, AIProvider.OpenAI, 2, Factory);
		await AIClient.CompleteAsync(MakeRequest(OpenAIEndpoint(), "m"));

		Assert.Equal(2, created);
	}

	[Fact]
	public async Task Complete_OverrideFactoryFailsOnce_RecoversOnNextCall() {
		var created = 0;
		AIClient.Init(null, null, AIProvider.OpenAI, 2, (_, _) => {
			created++;
			return created == 1 ? throw new InvalidOperationException("构造失败") : new CountingBackend();
		});
		var endpoint = OpenAIEndpoint();

		var first = await AIClient.CompleteAsync(MakeRequest(endpoint, "m"));
		var second = await AIClient.CompleteAsync(MakeRequest(endpoint, "m"));
		var third = await AIClient.CompleteAsync(MakeRequest(endpoint, "m"));

		Assert.Null(first);
		Assert.NotNull(second);
		Assert.NotNull(third);
		Assert.Equal(2, created);
	}

	[Fact]
	public async Task Complete_OverrideFactoryTransientFailure_RetriesConstruction() {
		var created = 0;
		AIClient.Init(null, null, AIProvider.OpenAI, 2, (_, _) => {
			created++;
			if (created == 1) throw new HttpRequestException("临时构造失败");
			return new CountingBackend();
		});

		var result = await AIClient.CompleteAsync(MakeRequest(OpenAIEndpoint(), "m"));

		Assert.NotNull(result);
		Assert.Equal(2, created);
	}

	[Fact]
	public async Task Complete_OverrideFactoryAlwaysFails_ReturnsNull() {
		var created = 0;
		AIClient.Init(null, null, AIProvider.OpenAI, 2, (_, _) => {
			created++;
			throw new InvalidOperationException("构造失败");
		});

		var result = await AIClient.CompleteAsync(MakeRequest(OpenAIEndpoint(), "m"));

		Assert.Null(result);
		Assert.Equal(1, created);
	}

	[Fact]
	public async Task Stream_OverrideFactoryFailure_PropagatesAndRecovers() {
		var created = 0;
		AIClient.Init(null, null, AIProvider.OpenAI, 2, (_, _) => {
			created++;
			if (created == 1) throw new InvalidOperationException("构造失败");
			return new CountingBackend();
		});

		await Assert.ThrowsAsync<InvalidOperationException>(async () => {
			await foreach (var _ in AIClient.StreamAsync(MakeRequest(OpenAIEndpoint(), "m"))) { }
		});

		var deltas = new List<AIStreamDelta>();
		await foreach (var delta in AIClient.StreamAsync(MakeRequest(OpenAIEndpoint(), "m"))) {
			deltas.Add(delta);
		}

		Assert.Equal("over", Assert.Single(deltas).Text);
		Assert.Equal(2, created);
	}

	[Fact]
	public async Task Complete_UninitializedStack_ReturnsNull_WithoutWaitingSlot() {
		var slotGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var blocker = new BlockingBackend(slotGate.Task);
		AIClient.Init(blocker, null, AIProvider.OpenAI, 1);
		var occupying = AIClient.CompleteAsync(MakeRequest());
		while (blocker.Calls == 0) {
			await Task.Delay(50);
		}

		using var cts = new CancellationTokenSource(200);
		var result = await AIClient.CompleteAsync(AIProvider.Gemini, MakeRequest(), cts.Token);

		Assert.Null(result);
		Assert.Equal(1, blocker.Calls);
		slotGate.SetResult();
		await occupying;
	}

	[Fact]
	public async Task Complete_ConcurrentCallsAfterFactoryFailure_AllRecover() {
		var created = 0;
		AIClient.Init(null, null, AIProvider.OpenAI, 4, (_, _) => {
			Interlocked.Increment(ref created);
			if (created == 1) throw new HttpRequestException("临时构造失败");
			return new CountingBackend();
		});

		var tasks = Enumerable.Range(0, 8).Select(_ => AIClient.CompleteAsync(MakeRequest(OpenAIEndpoint(), "m"))).ToArray();
		var results = await Task.WhenAll(tasks);

		Assert.All(results, static r => Assert.NotNull(r));
		Assert.True(created >= 2, $"created was {created}");
	}

	[Fact]
	public async Task Validate_EndpointWithoutModel_Throws() {
		AIClient.Init(null, null, AIProvider.OpenAI, 2);

		await Assert.ThrowsAsync<ArgumentException>(() => AIClient.CompleteAsync(MakeRequest(OpenAIEndpoint(), null)));
	}

	[Fact]
	public async Task Validate_OpenAIEndpoint_MissingApiKey_Throws() {
		AIClient.Init(null, null, AIProvider.OpenAI, 2);
		var endpoint = new AIEndpoint { BaseUrl = new Uri("https://api.example.com/v1") };

		await Assert.ThrowsAsync<ArgumentException>(() => AIClient.CompleteAsync(MakeRequest(endpoint, "m")));
	}

	[Fact]
	public async Task Validate_OpenAIEndpoint_MissingBaseUrl_Throws() {
		AIClient.Init(null, null, AIProvider.OpenAI, 2);
		var endpoint = new AIEndpoint { ApiKey = "k" };

		await Assert.ThrowsAsync<ArgumentException>(() => AIClient.CompleteAsync(MakeRequest(endpoint, "m")));
	}

	[Fact]
	public async Task Validate_OpenAIEndpoint_BadScheme_Throws() {
		AIClient.Init(null, null, AIProvider.OpenAI, 2);
		var endpoint = new AIEndpoint { ApiKey = "k", BaseUrl = new Uri("ftp://api.example.com/v1") };

		await Assert.ThrowsAsync<ArgumentException>(() => AIClient.CompleteAsync(MakeRequest(endpoint, "m")));
	}

	[Fact]
	public async Task Validate_OpenAIEndpoint_WithProject_Throws() {
		AIClient.Init(null, null, AIProvider.OpenAI, 2);
		var endpoint = new AIEndpoint { ApiKey = "k", BaseUrl = new Uri("https://api.example.com/v1"), Project = "p" };

		await Assert.ThrowsAsync<ArgumentException>(() => AIClient.CompleteAsync(MakeRequest(endpoint, "m")));
	}

	[Fact]
	public async Task Validate_GeminiEndpoint_KeyAndProject_Throws() {
		AIClient.Init(null, null, AIProvider.Gemini, 2);
		var endpoint = new AIEndpoint { ApiKey = "k", Project = "p" };

		await Assert.ThrowsAsync<ArgumentException>(() => AIClient.CompleteAsync(AIProvider.Gemini, MakeRequest(endpoint, "m")));
	}

	[Fact]
	public async Task Validate_GeminiEndpoint_ProjectWithoutLocation_Throws() {
		AIClient.Init(null, null, AIProvider.Gemini, 2);
		var endpoint = new AIEndpoint { Project = "p" };

		await Assert.ThrowsAsync<ArgumentException>(() => AIClient.CompleteAsync(AIProvider.Gemini, MakeRequest(endpoint, "m")));
	}

	[Fact]
	public async Task Validate_GeminiEndpoint_EmptyAuth_Throws() {
		AIClient.Init(null, null, AIProvider.Gemini, 2);
		var endpoint = new AIEndpoint();

		await Assert.ThrowsAsync<ArgumentException>(() => AIClient.CompleteAsync(AIProvider.Gemini, MakeRequest(endpoint, "m")));
	}

	[Fact]
	public async Task Complete_OpenAIStack_Sampling_PassesThrough() {
		var fake = new CountingBackend();
		AIClient.Init(fake, null, AIProvider.OpenAI, 2);
		var request = new AIRequest {
			Messages = [new AIMessage(AIRole.User, "hi")],
			Model = "m",
			Temperature = 0.5f,
			TopP = 0.9f,
		};

		var result = await AIClient.CompleteAsync(request);

		Assert.NotNull(result);
		Assert.Equal(1, fake.Calls);
		Assert.Equal(0.5f, fake.LastRequest!.Temperature);
		Assert.Equal(0.9f, fake.LastRequest!.TopP);
	}

	[Fact]
	public async Task Validate_ZeroMaxOutputTokens_Throws() {
		AIClient.Init(null, null, AIProvider.OpenAI, 2);
		var request = new AIRequest {
			Messages = [new AIMessage(AIRole.User, "hi")],
			Model = "m",
			MaxOutputTokens = 0,
		};

		await Assert.ThrowsAsync<ArgumentException>(() => AIClient.CompleteAsync(request));
	}

	[Fact]
	public async Task Validate_NegativeTemperature_Throws() {
		AIClient.Init(null, null, AIProvider.Gemini, 2);
		var request = new AIRequest {
			Messages = [new AIMessage(AIRole.User, "hi")],
			Model = "m",
			Temperature = -0.5f,
		};

		await Assert.ThrowsAsync<ArgumentException>(() => AIClient.CompleteAsync(request));
	}

	[Fact]
	public async Task Validate_OpenAIStack_MaxOutputTokens_Allowed() {
		var fake = new CountingBackend();
		AIClient.Init(fake, null, AIProvider.OpenAI, 2);
		var request = new AIRequest {
			Messages = [new AIMessage(AIRole.User, "hi")],
			Model = "m",
			MaxOutputTokens = 50,
		};

		var result = await AIClient.CompleteAsync(request);

		Assert.NotNull(result);
		Assert.Equal(1, fake.Calls);
		Assert.Equal(50, fake.LastRequest!.MaxOutputTokens);
	}

	[Fact]
	public async Task OpenAIBackend_Endpoint_SendsToBaseUrlWithKeyAndSampling() {
		var handler = new RecordingHandler(OpenAICompletionJson);
		var backend = new OpenAIBackend(OpenAIEndpoint("override-key"), new HttpClientPipelineTransport(new HttpClient(handler)));
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
		var backend = new OpenAIBackend(OpenAIEndpoint(), new HttpClientPipelineTransport(new HttpClient(handler)));

		await backend.CompleteAsync(MakeRequest(OpenAIEndpoint(), "m"), CancellationToken.None);

		var body = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement;
		Assert.False(body.TryGetProperty("max_completion_tokens", out _));
		Assert.False(body.TryGetProperty("temperature", out _));
		Assert.False(body.TryGetProperty("top_p", out _));
	}

	[Fact]
	public async Task OpenAIBackend_Stream_Sampling_SentInBody() {
		var handler = new RecordingHandler(OpenAIStreamSse, "text/event-stream");
		var backend = new OpenAIBackend(OpenAIEndpoint(), new HttpClientPipelineTransport(new HttpClient(handler)));
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
	public async Task GeminiBackend_Endpoint_SendsSamplingAndSystem() {
		var handler = new RecordingHandler(GeminiResponseJson);
		var backend = new GeminiBackend(GeminiKeyEndpoint("g-key"), new ClientOptions { HttpClientFactory = () => new HttpClient(handler) });
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
	public async Task GeminiBackend_Endpoint_BaseUrlOverride() {
		var handler = new RecordingHandler(GeminiResponseJson);
		var endpoint = new AIEndpoint { ApiKey = "g-key", BaseUrl = new Uri("https://gemini.example.com") };
		var backend = new GeminiBackend(endpoint, new ClientOptions { HttpClientFactory = () => new HttpClient(handler) });

		await backend.CompleteAsync(MakeRequest(null, "m"), CancellationToken.None);

		var sent = Assert.Single(handler.Requests);
		Assert.Equal("gemini.example.com", sent.RequestUri!.Host);
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

	private static AIRequest MakeRequest(AIEndpoint? endpoint = null, string? model = "m") {
		return new AIRequest {
			System = "sys",
			Messages = [new AIMessage(AIRole.User, "hi")],
			Model = model,
			Endpoint = endpoint,
		};
	}

	private static AIEndpoint OpenAIEndpoint(string apiKey = "k") {
		return new AIEndpoint { ApiKey = apiKey, BaseUrl = new Uri("https://api.example.com/v1") };
	}

	private static AIEndpoint GeminiKeyEndpoint(string apiKey = "g") {
		return new AIEndpoint { ApiKey = apiKey };
	}

	private class CountingBackend : IAIBackend {
		public int Calls;
		public AIRequest? LastRequest;

		public Task<AIResult?> CompleteAsync(AIRequest request, CancellationToken ct) {
			Calls++;
			LastRequest = request;
			return Task.FromResult<AIResult?>(new AIResult("over", new AIUsage(1, 2)));
		}

		public async IAsyncEnumerable<AIStreamDelta> StreamAsync(AIRequest request, [EnumeratorCancellation] CancellationToken ct) {
			Calls++;
			LastRequest = request;
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

	private sealed class BlockingBackend(Task gate) : IAIBackend {
		public int Calls;

		public async Task<AIResult?> CompleteAsync(AIRequest request, CancellationToken ct) {
			Interlocked.Increment(ref Calls);
			await Task.Yield();
			await gate.WaitAsync(ct);
			return new AIResult("ok", new AIUsage(1, 2));
		}

		public async IAsyncEnumerable<AIStreamDelta> StreamAsync(AIRequest request, [EnumeratorCancellation] CancellationToken ct) {
			await Task.Yield();
			yield return new AIStreamDelta("ok");
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
