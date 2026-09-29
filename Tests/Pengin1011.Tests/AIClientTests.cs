using Pengin1011.Services.AI;
using Pengin1011.Core;

namespace Pengin1011.Tests;

public sealed class AIClientTests {
	[Fact]
	public async Task Complete_RoutesToDefaultStack() {
		var openAI = new FakeBackend(static _ => Result("open"));
		var gemini = new FakeBackend(static _ => Result("gem"));
		AIClient.Init(openAI, gemini, AIProvider.Gemini, 2);

		var result = await AIClient.CompleteAsync(MakeRequest());

		Assert.NotNull(result);
		Assert.Equal("gem", result!.Text);
		Assert.Equal(1, gemini.Calls);
		Assert.Equal(0, openAI.Calls);
	}

	[Fact]
	public async Task Complete_ExplicitStackOverridesDefault() {
		var openAI = new FakeBackend(static _ => Result("open"));
		var gemini = new FakeBackend(static _ => Result("gem"));
		AIClient.Init(openAI, gemini, AIProvider.Gemini, 2);

		var result = await AIClient.CompleteAsync(AIProvider.OpenAI, MakeRequest());

		Assert.NotNull(result);
		Assert.Equal("open", result!.Text);
		Assert.Equal(1, openAI.Calls);
	}

	[Fact]
	public async Task Complete_UninitializedStack_ReturnsNull() {
		var openAI = new FakeBackend(static _ => Result("open"));
		AIClient.Init(openAI, null, AIProvider.OpenAI, 2);

		var result = await AIClient.CompleteAsync(AIProvider.Gemini, MakeRequest());

		Assert.Null(result);
		Assert.Equal(0, openAI.Calls);
	}

	[Fact]
	public async Task Complete_TransientFailure_RetriesThenSucceeds() {
		var fake = new FakeBackend(call => call == 1 ? throw new HttpRequestException("服务端临时失败") : Result("ok"));
		AIClient.Init(fake, null, AIProvider.OpenAI, 2);

		var result = await AIClient.CompleteAsync(MakeRequest());

		Assert.NotNull(result);
		Assert.Equal("ok", result!.Text);
		Assert.Equal(2, fake.Calls);
	}

	[Fact]
	public async Task Complete_TransientExhausted_ReturnsNull() {
		var fake = new FakeBackend(static _ => throw new HttpRequestException("服务端临时失败"));
		AIClient.Init(fake, null, AIProvider.OpenAI, 2);

		var result = await AIClient.CompleteAsync(MakeRequest());

		Assert.Null(result);
		Assert.Equal(AIClient.RetryCount + 1, fake.Calls);
	}

	[Fact]
	public async Task Complete_NonTransientError_SingleCallNoRetry() {
		var fake = new FakeBackend(static _ => throw new InvalidOperationException("参数错误"));
		AIClient.Init(fake, null, AIProvider.OpenAI, 2);

		var result = await AIClient.CompleteAsync(MakeRequest());

		Assert.Null(result);
		Assert.Equal(1, fake.Calls);
	}

	[Fact]
	public async Task Complete_CancelDuringBackoff_Propagates() {
		var fake = new FakeBackend(static _ => throw new HttpRequestException("限流"));
		AIClient.Init(fake, null, AIProvider.OpenAI, 2);
		using var cts = new CancellationTokenSource(50);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AIClient.CompleteAsync(MakeRequest(), cts.Token));
	}

	[Fact]
	public async Task Complete_CancelWhileWaitingSlot_Propagates() {
		var slotGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var blocker = new BlockingBackend(slotGate.Task);
		AIClient.Init(blocker, null, AIProvider.OpenAI, 1);
		var occupying = AIClient.CompleteAsync(MakeRequest());
		while (blocker.Calls == 0) {
			await Task.Delay(50);
		}

		using var cts = new CancellationTokenSource(50);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AIClient.CompleteAsync(MakeRequest(), cts.Token));

		slotGate.SetResult();
		await occupying;
	}

	[Fact]
	public async Task Complete_FailureReleasesSlot() {
		var fake = new FakeBackend(static _ => throw new InvalidOperationException("不可重试"));
		AIClient.Init(fake, null, AIProvider.OpenAI, 1);

		var first = await AIClient.CompleteAsync(MakeRequest());
		var second = await AIClient.CompleteAsync(MakeRequest());

		Assert.Null(first);
		Assert.Null(second);
		Assert.Equal(2, fake.Calls);
	}

	[Fact]
	public async Task Stream_MidStreamFailure_PropagatesWithoutRetry() {
		var fake = new FakeBackend(static _ => Result("ok")) {
			StreamThrowAfterFirst = true,
		};
		AIClient.Init(fake, null, AIProvider.OpenAI, 2);

		var deltas = new List<AIStreamDelta>();
		await Assert.ThrowsAsync<HttpRequestException>(async () => {
			await foreach (var delta in AIClient.StreamAsync(MakeRequest())) {
				deltas.Add(delta);
			}
		});

		Assert.Single(deltas);
	}

	[Fact]
	public async Task Complete_RespectsMaxParallel() {
		var fake = new FakeBackend(static _ => Result("ok"));
		AIClient.Init(fake, null, AIProvider.OpenAI, 2);

		var tasks = Enumerable.Range(0, 6).Select(static _ => AIClient.CompleteAsync(MakeRequest())).ToArray();
		var results = await Task.WhenAll(tasks);

		Assert.All(results, static r => Assert.NotNull(r));
		Assert.Equal(6, fake.Calls);
		Assert.True(fake.MaxConcurrent <= 2, $"max concurrent was {fake.MaxConcurrent}");
	}

	[Fact]
	public async Task Complete_PassesModelOverride() {
		var fake = new FakeBackend(static _ => Result("ok"));
		AIClient.Init(fake, null, AIProvider.OpenAI, 2);

		await AIClient.CompleteAsync(MakeRequest("custom-model"));

		Assert.Equal("custom-model", fake.LastModel);
	}

	[Fact]
	public void Init_ConfigFailure_ReleasesHalfBuiltBackends() {
		var config = new OpenAIConfig { ApiKey = "k", BaseUrl = new Uri("https://api.example.com"), Model = "m" };
		var geminiConfig = new GeminiConfig { Project = "p", Location = "l", Model = "m" };
		var clearedEnv = Environment.GetEnvironmentVariable("GOOGLE_GENAI_USE_ENTERPRISE") is null && Environment.GetEnvironmentVariable("GOOGLE_GENAI_USE_VERTEXAI") is null;
		if (!clearedEnv) {
			return;
		}
		Assert.ThrowsAny<Exception>(() => AIClient.Init(config, geminiConfig, AIProvider.OpenAI, 2));
	}

	private static AIResult Result(string text) {
		return new AIResult(text, new AIUsage(1, 2));
	}

	private static AIRequest MakeRequest(string? model = null) {
		return new AIRequest {
			System = "sys",
			Messages = [new AIMessage(AIRole.User, "hi")],
			Model = model,
		};
	}

	private sealed class FakeBackend(Func<int, AIResult?> handler) : IAIBackend {
		private readonly object _lock = new();
		public int Calls;
		private int Concurrent;
		public int MaxConcurrent;
		public string? LastModel;
		public bool StreamThrowAfterFirst;

		public async Task<AIResult?> CompleteAsync(AIRequest request, CancellationToken ct) {
			var call = Interlocked.Increment(ref Calls);
			LastModel = request.Model;
			lock (_lock) {
				Concurrent++;
				if (Concurrent > MaxConcurrent) MaxConcurrent = Concurrent;
			}
			try {
				await Task.Delay(80, ct);
				return handler(call);
			} finally {
				lock (_lock) {
					Concurrent--;
				}
			}
		}

		public async IAsyncEnumerable<AIStreamDelta> StreamAsync(AIRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
			await Task.Yield();
			yield return new AIStreamDelta("he");
			if (StreamThrowAfterFirst) {
				throw new HttpRequestException("流中途失败");
			}
			yield return new AIStreamDelta("llo", new AIUsage(3, 4));
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

		public async IAsyncEnumerable<AIStreamDelta> StreamAsync(AIRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
			await Task.Yield();
			yield return new AIStreamDelta("ok");
		}
	}
}
