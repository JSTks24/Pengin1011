using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

public sealed class AIClientTests {
	[Fact]
	public async Task Complete_RoutesToDefaultPreset() {
		var first = new FakeBackend(static _ => Result("one"));
		var second = new FakeBackend(static _ => Result("two"));
		InitPresets(("first", first), ("second", second));

		var result = await AIClient.CompleteAsync(MakeRequest());

		Assert.NotNull(result);
		Assert.Equal("one", result!.Text);
		Assert.Equal(1, first.Calls);
		Assert.Equal(0, second.Calls);
	}

	[Fact]
	public async Task Complete_NamedPresetRoutes() {
		var first = new FakeBackend(static _ => Result("one"));
		var second = new FakeBackend(static _ => Result("two"));
		InitPresets(("first", first), ("second", second));

		var result = await AIClient.CompleteAsync("second", MakeRequest());

		Assert.NotNull(result);
		Assert.Equal("two", result!.Text);
		Assert.Equal(1, second.Calls);
		Assert.Equal(0, first.Calls);
	}

	[Fact]
	public async Task Complete_UnknownPreset_ThrowsWithAvailableList() {
		var first = new FakeBackend(static _ => Result("one"));
		InitPresets(("first", first));

		var exception = await Assert.ThrowsAsync<AIPresetNotFoundException>(() => AIClient.CompleteAsync("missing", MakeRequest()));

		Assert.Contains("missing", exception.Message);
		Assert.Contains("first", exception.Message);
		Assert.Equal(0, first.Calls);
	}

	[Fact]
	public async Task Complete_NoPresetsAtAll_Throws() {
		var fake = new FakeBackend(static _ => Result("one"));
		AIPresetStore.LoadForTest(new AIPresetDocument());
		AIClient.Init(2, _ => fake);

		var exception = await Assert.ThrowsAsync<AIPresetNotFoundException>(() => AIClient.CompleteAsync(MakeRequest()));

		Assert.Contains(L.Prefix("AIPresetDefaultMissing"), exception.Message);
		Assert.Equal(0, fake.Calls);
	}

	[Fact]
	public void Stream_UnknownPreset_ThrowsWithoutInvokingBackend() {
		var fake = new FakeBackend(static _ => Result("one"));
		InitPresets(("first", fake));

		Assert.Throws<AIPresetNotFoundException>(() => AIClient.StreamAsync("missing", MakeRequest()));

		Assert.Equal(0, fake.Calls);
	}

	[Fact]
	public async Task Stream_NamedPresetRoutes() {
		var first = new FakeBackend(static _ => Result("one"));
		var second = new FakeBackend(static _ => Result("two"));
		InitPresets(("first", first), ("second", second));

		var deltas = new List<AIStreamDelta>();
		await foreach (var delta in AIClient.StreamAsync("second", MakeRequest())) {
			deltas.Add(delta);
		}

		Assert.Equal("hello", string.Concat(deltas.Select(delta => delta.Text)));
		Assert.Equal(1, second.Calls);
		Assert.Equal(0, first.Calls);
	}

	[Fact]
	public async Task Complete_PresetFillsMissingRequestFields() {
		var fake = new FakeBackend(static _ => Result("ok"));
		LoadSinglePreset(new AIPreset {
			Name = "first",
			Provider = AIPresetProvider.OpenAI,
			ApiKey = "k",
			BaseUrl = "https://api.example.com/v1",
			Model = "preset-model",
			Temperature = 0.7f,
			TopP = 0.9f,
			MaxOutputTokens = 123,
		}, fake);

		await AIClient.CompleteAsync(MakeRequest());

		Assert.Equal("preset-model", fake.LastModel);
		Assert.Equal(0.7f, fake.LastTemperature);
		Assert.Equal(0.9f, fake.LastTopP);
		Assert.Equal(123, fake.LastMaxOutputTokens);
	}

	[Fact]
	public async Task Complete_RequestOverridesPresetFields() {
		var fake = new FakeBackend(static _ => Result("ok"));
		LoadSinglePreset(new AIPreset {
			Name = "first",
			Provider = AIPresetProvider.OpenAI,
			ApiKey = "k",
			BaseUrl = "https://api.example.com/v1",
			Model = "preset-model",
			Temperature = 0.7f,
			TopP = 0.9f,
			MaxOutputTokens = 123,
		}, fake);

		await AIClient.CompleteAsync(MakeRequest(model: "custom-model", temperature: 0.1f, topP: 0.2f, maxOutputTokens: 45));

		Assert.Equal("custom-model", fake.LastModel);
		Assert.Equal(0.1f, fake.LastTemperature);
		Assert.Equal(0.2f, fake.LastTopP);
		Assert.Equal(45, fake.LastMaxOutputTokens);
	}

	[Fact]
	public async Task Complete_TransientFailure_RetriesThenSucceeds() {
		var fake = new FakeBackend(call => call == 1 ? throw new HttpRequestException("服务端临时失败") : Result("ok"));
		InitPresets(("first", fake));

		var result = await AIClient.CompleteAsync(MakeRequest());

		Assert.NotNull(result);
		Assert.Equal("ok", result!.Text);
		Assert.Equal(2, fake.Calls);
	}

	[Fact]
	public async Task Complete_TransientExhausted_ReturnsNull() {
		var fake = new FakeBackend(static _ => throw new HttpRequestException("服务端临时失败"));
		InitPresets(("first", fake));

		var result = await AIClient.CompleteAsync(MakeRequest());

		Assert.Null(result);
		Assert.Equal(AIClient.RetryCount + 1, fake.Calls);
	}

	[Fact]
	public async Task Complete_NonTransientError_SingleCallNoRetry() {
		var fake = new FakeBackend(static _ => throw new InvalidOperationException("参数错误"));
		InitPresets(("first", fake));

		var result = await AIClient.CompleteAsync(MakeRequest());

		Assert.Null(result);
		Assert.Equal(1, fake.Calls);
	}

	[Fact]
	public async Task Complete_BackendFactoryThrows_TreatedAsTransientFailure() {
		var calls = 0;
		AIPresetStore.LoadForTest(new AIPresetDocument { Presets = [MakePreset("first")] });
		AIClient.Init(2, _ => {
			calls++;
			throw new HttpRequestException("构造后端失败");
		});

		var result = await AIClient.CompleteAsync(MakeRequest());

		Assert.Null(result);
		Assert.Equal(AIClient.RetryCount + 1, calls);
	}

	[Fact]
	public async Task Complete_CancelDuringBackoff_Propagates() {
		var fake = new FakeBackend(static _ => throw new HttpRequestException("限流"));
		InitPresets(("first", fake));
		using var cts = new CancellationTokenSource(50);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AIClient.CompleteAsync(MakeRequest(), cts.Token));
	}

	[Fact]
	public async Task Complete_CancelWhileWaitingSlot_Propagates() {
		var slotGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var blocker = new BlockingBackend(slotGate.Task);
		InitPresets(("first", blocker));
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
		InitPresets(("first", fake));

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
		InitPresets(("first", fake));

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
		InitPresets(("first", fake));

		var tasks = Enumerable.Range(0, 6).Select(static _ => AIClient.CompleteAsync(MakeRequest())).ToArray();
		var results = await Task.WhenAll(tasks);

		Assert.All(results, static r => Assert.NotNull(r));
		Assert.Equal(6, fake.Calls);
		Assert.True(fake.MaxConcurrent <= 2, $"max concurrent was {fake.MaxConcurrent}");
	}

	[Fact]
	public async Task Complete_ReusesBackendInstanceForSamePreset() {
		var created = 0;
		AIPresetStore.LoadForTest(new AIPresetDocument { Presets = [MakePreset("first")] });
		AIClient.Init(2, _ => {
			created++;
			return new FakeBackend(static _ => Result("ok"));
		});

		await AIClient.CompleteAsync(MakeRequest());
		await AIClient.CompleteAsync(MakeRequest());

		Assert.Equal(1, created);
	}

	[Fact]
	public async Task Complete_InvalidateBackendCache_CreatesFreshBackend() {
		var created = 0;
		AIPresetStore.LoadForTest(new AIPresetDocument { Presets = [MakePreset("first")] });
		AIClient.Init(2, _ => {
			created++;
			return new FakeBackend(static _ => Result("ok"));
		});

		await AIClient.CompleteAsync(MakeRequest());
		AIClient.InvalidateBackendCache();
		await AIClient.CompleteAsync(MakeRequest());

		Assert.Equal(2, created);
	}

	private static AIResult Result(string text) {
		return new AIResult(text, new AIUsage(1, 2));
	}

	private static AIPreset MakePreset(string name) {
		return new AIPreset {
			Name = name,
			Provider = AIPresetProvider.OpenAI,
			ApiKey = "key-" + name,
			BaseUrl = "https://api.example.com/v1",
			Model = "model-" + name,
		};
	}

	private static AIRequest MakeRequest(string? model = null, float? temperature = null, float? topP = null, int? maxOutputTokens = null) {
		return new AIRequest {
			System = "sys",
			Messages = [new AIMessage(AIRole.User, "hi")],
			Model = model,
			Temperature = temperature,
			TopP = topP,
			MaxOutputTokens = maxOutputTokens,
		};
	}

	private static void InitPresets(params (string Name, IAIBackend Backend)[] entries) {
		AIPresetStore.LoadForTest(new AIPresetDocument { MaxParallel = 2, Presets = [.. entries.Select(entry => MakePreset(entry.Name))] });
		var map = entries.ToDictionary(entry => entry.Name, entry => entry.Backend);
		AIClient.Init(2, preset => map[preset.Name]);
	}

	private static void LoadSinglePreset(AIPreset preset, IAIBackend backend) {
		AIPresetStore.LoadForTest(new AIPresetDocument { MaxParallel = 2, Presets = [preset] });
		AIClient.Init(2, _ => backend);
	}

	private sealed class FakeBackend(Func<int, AIResult?> handler) : IAIBackend {
		private readonly object _lock = new();
		public int Calls;
		private int _concurrent;
		public int MaxConcurrent;
		public string? LastModel;
		public float? LastTemperature;
		public float? LastTopP;
		public int? LastMaxOutputTokens;
		public bool StreamThrowAfterFirst;

		public async Task<AIResult?> CompleteAsync(AIRequest request, CancellationToken ct) {
			var call = Interlocked.Increment(ref Calls);
			LastModel = request.Model;
			LastTemperature = request.Temperature;
			LastTopP = request.TopP;
			LastMaxOutputTokens = request.MaxOutputTokens;
			lock (_lock) {
				_concurrent++;
				if (_concurrent > MaxConcurrent) MaxConcurrent = _concurrent;
			}
			try {
				await Task.Delay(80, ct);
				return handler(call);
			} finally {
				lock (_lock) {
					_concurrent--;
				}
			}
		}

		public async IAsyncEnumerable<AIStreamDelta> StreamAsync(AIRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
			await Task.Yield();
			Interlocked.Increment(ref Calls);
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
