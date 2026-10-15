using System.Runtime.CompilerServices;
using Pengin1011.Helper;
using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

public sealed class AIConnectionLeaseTests : IDisposable {
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"pengin1011_leases_{Guid.NewGuid():N}");
	private readonly string _path;

	public AIConnectionLeaseTests() {
		Directory.CreateDirectory(_directory);
		_path = Path.Combine(_directory, "aipresets.json");
		AIPresetUsageRegistry.ForgetAll();
	}

	public void Dispose() {
		AIClient.Shutdown();
		AIPresetStore.ResetForTest();
		AIPresetUsageRegistry.ForgetAll();
		Directory.Delete(_directory, true);
	}

	private sealed class TrackingBackend : IAIBackend, IDisposable {
		public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int DisposeCount;
		public int Calls;
		public bool Block = true;
		public string? LastModel;
		public Action? OnDispose;

		public async Task<AIResult?> CompleteAsync(AIRequest request, CancellationToken ct) {
			Interlocked.Increment(ref Calls);
			LastModel = request.Model;
			Entered.TrySetResult();
			if (Block) await Release.Task.WaitAsync(ct);
			return new AIResult("ok", new AIUsage(1, 2));
		}

		public async IAsyncEnumerable<AIStreamDelta> StreamAsync(AIRequest request, [EnumeratorCancellation] CancellationToken ct) {
			Interlocked.Increment(ref Calls);
			await Task.Yield();
			yield return new AIStreamDelta("ok");
		}

		public void Dispose() {
			Interlocked.Increment(ref DisposeCount);
			OnDispose?.Invoke();
		}
	}

	private static AIPreset Preset(string name = "prod", string apiKey = "key-a") {
		return new AIPreset {
			Name = name,
			Provider = AIPresetProvider.OpenAI,
			ApiKey = apiKey,
			BaseUrl = "https://api.example.com/v1",
			Model = "m",
		};
	}

	private static AIRequest Request() {
		return new AIRequest { Messages = [new AIMessage(AIRole.User, "hi")] };
	}

	private void Seed(params AIPreset[] presets) {
		Assert.True(JsonHelper.Save(_path, new AIPresetDocument { Presets = presets }));
		Assert.True(AIPresetStore.TryLoad(_path, out var errors), string.Join("; ", errors));
	}

	private static void ChangeKey(string apiKey) {
		Assert.True(AIPresetStore.Apply("prod", new AIPresetPatch { ApiKey = Patch<string>.SetTo(apiKey) }, null, out var result), result.Error);
	}

	[Fact]
	public async Task A14_RealPatchCommit_RetiresOnlyAfterInFlightCallEnds() {
		var backend = new TrackingBackend();
		Seed(Preset());
		AIClient.Init(2, preset => preset.ApiKey == "key-a" ? backend : new TrackingBackend { Block = false });
		var inFlight = AIClient.CompleteAsync(Request());
		try {
			await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

			ChangeKey("key-b");

			Assert.Equal("key-b", JsonHelper.Load<AIPresetDocument>(_path).Presets[0].ApiKey);
			Assert.NotNull(await AIClient.CompleteAsync(Request()));
			Assert.Equal(0, backend.DisposeCount);
		} finally {
			backend.Release.TrySetResult();
			await inFlight;
		}
		Assert.Equal(1, backend.DisposeCount);
	}

	[Fact]
	public async Task A14_LateAdmissionAfterKeyChange_UsesTheNewConnection() {
		var backend = new TrackingBackend();
		var created = new List<string>();
		Seed(Preset());
		AIClient.Init(1, preset => {
			created.Add(preset.ApiKey);
			return preset.ApiKey == "key-a" ? backend : new TrackingBackend { Block = false };
		});
		var first = AIClient.CompleteAsync(Request());
		Task<AIResult?>? waiting = null;
		try {
			await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			waiting = AIClient.CompleteAsync(Request());
			ChangeKey("key-b");
		} finally {
			backend.Release.TrySetResult();
			await first;
			if (waiting != null) await waiting;
		}
		Assert.Equal(["key-a", "key-b"], created);
	}

	[Fact]
	public async Task A17_ModelOnlyPatch_ReusesTheSameConnection() {
		var backend = new TrackingBackend { Block = false };
		var created = 0;
		Seed(Preset());
		AIClient.Init(2, _ => {
			Interlocked.Increment(ref created);
			return backend;
		});
		await AIClient.CompleteAsync(Request());

		Assert.True(AIPresetStore.Apply("prod", new AIPresetPatch { Model = Patch<string>.SetTo("m2") }, null, out var result), result.Error);
		await AIClient.CompleteAsync(Request());

		Assert.Equal(1, created);
		Assert.Equal(0, backend.DisposeCount);
		Assert.Equal("m2", backend.LastModel);
	}

	[Fact]
	public async Task A16_ConcurrentConstruction_PublishesExactlyOneUsableInstance() {
		var created = 0;
		Seed(Preset());
		AIClient.Init(4, _ => {
			Interlocked.Increment(ref created);
			return new TrackingBackend { Block = false };
		});

		var tasks = Enumerable.Range(0, 8).Select(_ => AIClient.CompleteAsync(Request())).ToArray();
		var results = await Task.WhenAll(tasks);

		Assert.All(results, result => Assert.NotNull(result));
		Assert.Equal(1, created);
	}

	[Fact]
	public async Task A16_FactoryFailure_DoesNotPoisonTheCache() {
		var attempts = 0;
		Seed(Preset());
		AIClient.Init(2, _ => {
			if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("构造失败");
			return new TrackingBackend { Block = false };
		});

		Assert.Null(await AIClient.CompleteAsync(Request()));
		Assert.NotNull(await AIClient.CompleteAsync(Request()));
		Assert.Equal(2, attempts);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task A16_LateOldCreation_DoesNotPublishIntoOrRemoveNewEntry(bool failFirst) {
		var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var firstBackend = new TrackingBackend { Block = false };
		var secondBackend = new TrackingBackend { Block = false };
		var created = 0;
		Seed(Preset());
		AIClient.Init(3, _ => {
			if (Interlocked.Increment(ref created) == 1) {
				firstEntered.TrySetResult();
				firstRelease.Task.GetAwaiter().GetResult();
				if (failFirst) throw new InvalidOperationException("旧创建失败");
				return firstBackend;
			}
			secondEntered.TrySetResult();
			secondRelease.Task.GetAwaiter().GetResult();
			return secondBackend;
		});
		var first = AIClient.CompleteAsync(Request());
		Task<AIResult?>? second = null;
		try {
			await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			AIClient.InvalidateBackendCache();
			second = AIClient.CompleteAsync(Request());
			await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			firstRelease.TrySetResult();
			var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
			if (failFirst) {
				Assert.Null(firstResult);
			} else {
				Assert.NotNull(firstResult);
				Assert.Equal(1, firstBackend.Calls);
				Assert.Equal(1, firstBackend.DisposeCount);
			}
			Assert.Equal(0, secondBackend.Calls);
			Assert.Equal(0, secondBackend.DisposeCount);
			secondRelease.TrySetResult();
			Assert.NotNull(await second.WaitAsync(TimeSpan.FromSeconds(5)));
			Assert.NotNull(await AIClient.CompleteAsync(Request()));
			Assert.Equal(2, created);
			Assert.Equal(2, secondBackend.Calls);
		} finally {
			firstRelease.TrySetResult();
			secondRelease.TrySetResult();
			await first;
			if (second != null) await second;
		}
	}

	[Fact]
	public async Task A19_RetryKeepsSnapshotAndLeaseAcrossRealConfigurationCommit() {
		var models = new List<string?>();
		var created = new List<string>();
		var retryBackend = new RetryBackend(request => {
			models.Add(request.Model);
			if (models.Count != 1) return;
			Assert.True(AIPresetStore.Apply("prod", new AIPresetPatch { ApiKey = Patch<string>.SetTo("key-b"), Model = Patch<string>.SetTo("m2") }, null, out var result), result.Error);
			throw new HttpRequestException("临时失败");
		});
		Seed(Preset());
		AIClient.Init(2, preset => {
			created.Add(preset.ApiKey);
			return preset.ApiKey == "key-a" ? retryBackend : new TrackingBackend { Block = false };
		});

		Assert.NotNull(await AIClient.CompleteAsync(Request()));

		Assert.Equal(["m", "m"], models);
		Assert.Equal(["key-a"], created);
		Assert.Equal(1, retryBackend.DisposeCount);
		Assert.NotNull(await AIClient.CompleteAsync(Request()));
		Assert.Equal(["key-a", "key-b"], created);
	}

	[Fact]
	public async Task A20_ShutdownCountsRetiredAndCurrentInFlightGenerations() {
		var firstBackend = new TrackingBackend();
		var secondBackend = new TrackingBackend();
		Seed(Preset());
		AIClient.Init(2, preset => preset.ApiKey == "key-a" ? firstBackend : secondBackend);
		var first = AIClient.CompleteAsync(Request());
		Task<AIResult?>? second = null;
		try {
			await firstBackend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			ChangeKey("key-b");
			second = AIClient.CompleteAsync(Request());
			await secondBackend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

			var both = await AIClient.ShutdownAsync(TimeSpan.FromMilliseconds(60));
			Assert.False(both.Drained);
			Assert.True(both.ActiveLeases >= 2);
			Assert.Equal(0, firstBackend.DisposeCount);
			Assert.Equal(0, secondBackend.DisposeCount);
			await Assert.ThrowsAsync<InvalidOperationException>(() => AIClient.CompleteAsync(Request()));

			secondBackend.Release.TrySetResult();
			Assert.NotNull(await second);
			var oldOnly = await AIClient.ShutdownAsync(TimeSpan.FromMilliseconds(60));
			Assert.False(oldOnly.Drained);
			Assert.True(oldOnly.ActiveLeases >= 1);
			Assert.Equal(0, firstBackend.DisposeCount);
		} finally {
			firstBackend.Release.TrySetResult();
			secondBackend.Release.TrySetResult();
			await first;
			if (second != null) await second;
		}
		Assert.True((await AIClient.ShutdownAsync(TimeSpan.FromSeconds(5))).Drained);
		Assert.Equal(1, firstBackend.DisposeCount);
		Assert.Equal(1, secondBackend.DisposeCount);
	}

	[Fact]
	public async Task A20_CancelledCreationWaiter_DoesNotHidePendingConstructionFromShutdown() {
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var backend = new TrackingBackend { Block = false };
		Seed(Preset());
		AIClient.Init(2, _ => {
			entered.TrySetResult();
			release.Task.GetAwaiter().GetResult();
			return backend;
		});
		using var cts = new CancellationTokenSource();
		var request = AIClient.CompleteAsync(Request(), cts.Token);
		try {
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			cts.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

			var shutdown = await AIClient.ShutdownAsync(TimeSpan.FromMilliseconds(60));
			Assert.False(shutdown.Drained);
			Assert.True(shutdown.ActiveLeases >= 1);
			Assert.Equal(0, backend.DisposeCount);
		} finally {
			release.TrySetResult();
		}
		Assert.True((await AIClient.ShutdownAsync(TimeSpan.FromSeconds(5))).Drained);
		Assert.Equal(1, backend.DisposeCount);
	}

	[Fact]
	public async Task A17_RemovingOnePresetWithSharedConnection_KeepsBackendUntilLastPresetRemoved() {
		var backend = new TrackingBackend { Block = false };
		var created = 0;
		Seed(Preset(), Preset("shared"));
		AIClient.Init(2, _ => {
			created++;
			return backend;
		});
		await AIClient.CompleteAsync(Request());

		Assert.True(AIPresetStore.RemovePatch("prod", null, out var firstResult), firstResult.Error);
		Assert.Equal(0, backend.DisposeCount);
		Assert.NotNull(await AIClient.CompleteAsync(Request()));
		Assert.Equal(1, created);
		Assert.True(AIPresetStore.RemovePatch("shared", null, out var lastResult), lastResult.Error);
		Assert.Equal(1, backend.DisposeCount);
	}

	[Fact]
	public async Task A20_ShutdownBudgetIncludesBackendDisposal() {
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var backend = new TrackingBackend {
			Block = false,
			OnDispose = () => {
				entered.TrySetResult();
				release.Task.GetAwaiter().GetResult();
			},
		};
		Seed(Preset());
		AIClient.Init(2, _ => backend);
		await AIClient.CompleteAsync(Request());
		try {
			var stopping = AIClient.ShutdownAsync(TimeSpan.FromMilliseconds(60));
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			var result = await stopping.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.False(result.Drained);
			Assert.True(result.ActiveLeases >= 1);
		} finally {
			release.TrySetResult();
		}
		Assert.True((await AIClient.ShutdownAsync(TimeSpan.FromSeconds(5))).Drained);
		Assert.Equal(1, backend.DisposeCount);
	}

	[Fact]
	public async Task A23_DisposalFailure_DoesNotReportPersistedCommitAsFailure() {
		var backend = new TrackingBackend { Block = false, OnDispose = () => throw new InvalidOperationException("释放失败") };
		Seed(Preset());
		AIClient.Init(2, _ => backend);
		await AIClient.CompleteAsync(Request());

		Assert.True(AIPresetStore.Apply("prod", new AIPresetPatch { ApiKey = Patch<string>.SetTo("key-b") }, null, out var result), result.Error);

		Assert.True(result.Success);
		Assert.Equal("key-b", JsonHelper.Load<AIPresetDocument>(_path).Presets[0].ApiKey);
		Assert.Equal(1, backend.DisposeCount);
	}

	private sealed class RetryBackend(Action<AIRequest> call) : IAIBackend, IDisposable {
		public int DisposeCount;

		public Task<AIResult?> CompleteAsync(AIRequest request, CancellationToken ct) {
			call(request);
			return Task.FromResult<AIResult?>(new AIResult("ok", new AIUsage(1, 2)));
		}

		public async IAsyncEnumerable<AIStreamDelta> StreamAsync(AIRequest request, [EnumeratorCancellation] CancellationToken ct) {
			await Task.Yield();
			yield return new AIStreamDelta("ok");
		}

		public void Dispose() {
			Interlocked.Increment(ref DisposeCount);
		}
	}
}
