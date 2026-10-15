using System.Reflection;
using Pengin1011.Core;
using Pengin1011.Core.Modules;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ModuleBridgeGeneratedProxyTests {
	private sealed class StubRuntime : IModuleRuntime {
		public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
		public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
	}

	private const string ProviderSource = """
public static class Exports {
	public static System.Threading.Tasks.TaskCompletionSource<int> Completion { get; } = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
	[Pengin1011.Core.ModuleBridgeExport]
	public static int Value() => 3;
	[Pengin1011.Core.ModuleBridgeExport]
	public static void Fire() { }
	[Pengin1011.Core.ModuleBridgeExport]
	public static int Fail() => throw new System.InvalidOperationException("failed");
	[Pengin1011.Core.ModuleBridgeExport]
	public static System.Threading.Tasks.Task<int> Slow() => Completion.Task;
	[Pengin1011.Core.ModuleBridgeExport]
	public static System.Threading.Tasks.Task<int> FailedTask() => System.Threading.Tasks.Task.FromException<int>(new System.InvalidOperationException("task failed"));
	[Pengin1011.Core.ModuleBridgeExport]
	public static System.Threading.Tasks.Task<int> CancelledTask() => System.Threading.Tasks.Task.FromCanceled<int>(new System.Threading.CancellationToken(true));
	[Pengin1011.Core.ModuleBridgeExport]
	public static System.Threading.Tasks.ValueTask<int> Later() => new(5);
	[Pengin1011.Core.ModuleBridgeExport]
	public static System.Collections.Generic.IEnumerable<int> Sequence() {
		yield return 7;
		yield return 8;
	}
	[Pengin1011.Core.ModuleBridgeExport]
	public static System.Collections.Generic.IEnumerable<int> BrokenSequence() {
		yield return 7;
		throw new System.InvalidOperationException("enumeration failed");
	}
	[Pengin1011.Core.ModuleBridgeExport]
	public static async System.Collections.Generic.IAsyncEnumerable<int> Stream([System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		yield return 11;
		await System.Threading.Tasks.Task.Delay(-1, ct);
	}
}
""";

	private const string ConsumerSource = """
public static class Consumer {
	public static int Value() => Pengin1011.Core.ModuleBridge.GeneratedDonor.Value();
	public static void Fire() => Pengin1011.Core.ModuleBridge.GeneratedDonor.Fire();
	public static int Fail() => Pengin1011.Core.ModuleBridge.GeneratedDonor.Fail();
	public static System.Threading.Tasks.Task<int> Slow() => Pengin1011.Core.ModuleBridge.GeneratedDonor.Slow();
	public static System.Threading.Tasks.Task<int> FailedTask() => Pengin1011.Core.ModuleBridge.GeneratedDonor.FailedTask();
	public static System.Threading.Tasks.Task<int> CancelledTask() => Pengin1011.Core.ModuleBridge.GeneratedDonor.CancelledTask();
	public static System.Threading.Tasks.ValueTask<int> Later() => Pengin1011.Core.ModuleBridge.GeneratedDonor.Later();
	public static System.Collections.Generic.IEnumerable<int> Sequence() => Pengin1011.Core.ModuleBridge.GeneratedDonor.Sequence();
	public static System.Collections.Generic.IEnumerable<int> BrokenSequence() => Pengin1011.Core.ModuleBridge.GeneratedDonor.BrokenSequence();
	public static System.Collections.Generic.IAsyncEnumerable<int> Stream() => Pengin1011.Core.ModuleBridge.GeneratedDonor.Stream(default);
}
""";

	private static (LoadedModule Run, Type Consumer, TaskCompletionSource<int> Completion) Register() {
		ModuleBridgeDispatcher.ForgetAll();
		var provider = GeneratorTestHost.Run(ProviderSource, "GeneratedDonor");
		GeneratorTestHost.AssertNoGeneratorDiagnostics(provider);
		var assembly = Load(provider);
		var run = new LoadedModule {
			Name = "GeneratedDonor",
			FilePath = "",
			Assembly = assembly,
			LoadedAt = DateTimeOffset.Now,
			Runtime = new StubRuntime(),
			State = ModuleState.Ready,
		};
		ModuleBridgeDispatcher.RegisterModule(assembly, run);
		var consumer = GeneratorTestHost.Run(ConsumerSource, "GeneratedConsumer_" + Guid.NewGuid().ToString("N"), ("ModuleBridgeManifest.g.cs", provider.SourceByHint("ModuleBridgeManifest.g.cs")));
		GeneratorTestHost.AssertNoGeneratorDiagnostics(consumer);
		var consumerType = Load(consumer).GetType("Consumer")!;
		var completion = (TaskCompletionSource<int>)assembly.GetType("Exports")!.GetProperty("Completion")!.GetValue(null)!;
		return (run, consumerType, completion);
	}

	private static Assembly Load(GeneratorOutcome outcome) {
		GeneratorTestHost.AssertCompiles(outcome);
		using var stream = new MemoryStream();
		var result = outcome.OutputCompilation.Emit(stream);
		Assert.True(result.Success, string.Join(" | ", result.Diagnostics));
		return Assembly.Load(stream.ToArray());
	}

	private static T Invoke<T>(Type consumer, string name) {
		return (T)consumer.GetMethod(name)!.Invoke(null, null)!;
	}

	[Fact]
	public async Task GeneratedProxy_HoldsTaskAndReleasesAllImmediateResults() {
		var (run, consumer, completion) = Register();
		try {
			Assert.Equal(3, Invoke<int>(consumer, "Value"));
			consumer.GetMethod("Fire")!.Invoke(null, null);
			Assert.Equal(5, await Invoke<ValueTask<int>>(consumer, "Later"));
			Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => Invoke<int>(consumer, "Fail")).InnerException);
			await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke<Task<int>>(consumer, "FailedTask"));
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke<Task<int>>(consumer, "CancelledTask"));
			Assert.Equal(0, ModuleBridgeDispatcher.ActiveCallsOf(run));

			var pending = Invoke<Task<int>>(consumer, "Slow");
			Assert.Equal(1, ModuleBridgeDispatcher.ActiveCallsOf(run));
			var stopping = ModuleHost.StopAsync(run);
			Assert.False(stopping.IsCompleted);
			Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => Invoke<int>(consumer, "Value")).InnerException);
			completion.SetResult(13);
			Assert.Equal(13, await pending);
			var stopped = await stopping;
			Assert.True(stopped.Clean, stopped.Reason);
			Assert.Equal(0, ModuleBridgeDispatcher.ActiveCallsOf(run));
		} finally {
			completion.TrySetResult(0);
			ModuleBridgeDispatcher.ForgetAll();
		}
	}

	[Fact]
	public async Task GeneratedSequence_AdmitsEachEnumerationAndReleasesOnDisposeAndFailure() {
		var (run, consumer, _) = Register();
		try {
			var sequence = Invoke<IEnumerable<int>>(consumer, "Sequence");
			Assert.Equal(0, ModuleBridgeDispatcher.ActiveCallsOf(run));
			using (var first = sequence.GetEnumerator()) {
				using (var second = sequence.GetEnumerator()) {
					Assert.True(first.MoveNext());
					Assert.True(second.MoveNext());
					Assert.Equal(2, ModuleBridgeDispatcher.ActiveCallsOf(run));
				}
				Assert.Equal(1, ModuleBridgeDispatcher.ActiveCallsOf(run));
			}
			Assert.Equal(0, ModuleBridgeDispatcher.ActiveCallsOf(run));
			Assert.Equal(new[] { 7, 8 }, sequence.ToArray());
			Assert.Equal(0, ModuleBridgeDispatcher.ActiveCallsOf(run));
			Assert.Throws<InvalidOperationException>(() => Invoke<IEnumerable<int>>(consumer, "BrokenSequence").ToArray());
			Assert.Equal(0, ModuleBridgeDispatcher.ActiveCallsOf(run));
			var stopped = await ModuleHost.StopAsync(run);
			Assert.True(stopped.Clean, stopped.Reason);
			Assert.Throws<InvalidOperationException>(() => sequence.ToArray());
			Assert.Equal(0, ModuleBridgeDispatcher.ActiveCallsOf(run));
		} finally {
			ModuleBridgeDispatcher.ForgetAll();
		}
	}

	[Fact]
	public async Task GeneratedStream_ForwardsEnumerationCancellationAndReleasesEachLease() {
		var (run, consumer, _) = Register();
		try {
			var sequence = Invoke<IAsyncEnumerable<int>>(consumer, "Stream");
			Assert.Equal(0, ModuleBridgeDispatcher.ActiveCallsOf(run));
			using var cancellation = new CancellationTokenSource();
			await using (var first = sequence.GetAsyncEnumerator(cancellation.Token)) {
				await using (var second = sequence.GetAsyncEnumerator()) {
					Assert.True(await first.MoveNextAsync());
					Assert.True(await second.MoveNextAsync());
					Assert.Equal(2, ModuleBridgeDispatcher.ActiveCallsOf(run));
				}
				Assert.Equal(1, ModuleBridgeDispatcher.ActiveCallsOf(run));
				cancellation.Cancel();
				await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first.MoveNextAsync());
				Assert.Equal(0, ModuleBridgeDispatcher.ActiveCallsOf(run));
			}
			var stopped = await ModuleHost.StopAsync(run);
			Assert.True(stopped.Clean, stopped.Reason);
			await using var rejected = sequence.GetAsyncEnumerator();
			await Assert.ThrowsAsync<InvalidOperationException>(async () => await rejected.MoveNextAsync());
			Assert.Equal(0, ModuleBridgeDispatcher.ActiveCallsOf(run));
		} finally {
			ModuleBridgeDispatcher.ForgetAll();
		}
	}

	[Fact]
	public async Task GeneratedPendingCall_BlocksStopUntilItsTaskFinishes() {
		var (run, consumer, completion) = Register();
		ModuleHost.StopTimeout = TimeSpan.FromMilliseconds(300);
		try {
			var pending = Invoke<Task<int>>(consumer, "Slow");
			var result = await ModuleHost.StopAsync(run);

			Assert.Equal(ModuleStopOutcome.PendingTimeout, result.Outcome);
			Assert.False(pending.IsCompleted);
			Assert.Equal(1, ModuleBridgeDispatcher.ActiveCallsOf(run));
			completion.SetResult(17);
			Assert.Equal(17, await pending);
			var stopped = await ModuleHost.StopAsync(run);
			Assert.True(stopped.Clean, stopped.Reason);
			Assert.Equal(0, ModuleBridgeDispatcher.ActiveCallsOf(run));
		} finally {
			completion.TrySetResult(0);
			ModuleHost.StopTimeout = TimeSpan.FromSeconds(10);
			ModuleBridgeDispatcher.ForgetAll();
		}
	}
}
