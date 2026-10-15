using Pengin1011.Core;
using Pengin1011.Core.Modules;
using Pengin1011.Modules.FakeModule;
using Pengin1011.Modules.FakeSecond;
using Pengin1011.Modules.FakeThird;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ModuleBridgeTests {
	private static async Task LoadAndStartAllAsync() {
		ModuleHostTests.ResetModules();
		ModuleHost.LoadAll();
		foreach (var run in ModuleHost.Modules) {
			ModuleHost.StartRuntime(run);
		}
		foreach (var run in ModuleHost.Modules) {
			if (run.InitTask != null) await run.InitTask;
			await ModuleHostTests.WaitStateAsync(run, ModuleState.Ready);
		}
	}

	[Fact]
	public async Task StaticExport_AcrossModules_ReturnsValueAndCountsInvocation() {
		FakeModuleBridgeExports.ResetCalls();
		try {
			await LoadAndStartAllAsync();

			var greeting = await FakeThirdBridge.QueryGreeting(7);

			Assert.Equal("bridge-hello-7", greeting);
			Assert.Equal(1, FakeModuleBridgeExports.StaticCallCount);
		} finally {
			ModuleHostTests.ResetModules();
		}
	}

	[Fact]
	public async Task InstanceExport_BindsToRuntimeInstance() {
		try {
			await LoadAndStartAllAsync();

			var state = FakeThirdBridge.DescribeRuntimeState();

			Assert.StartsWith("FakeModuleRuntime-", state);
		} finally {
			ModuleHostTests.ResetModules();
		}
	}

	[Fact]
	public async Task SecondDependency_IsCallableFromSameConsumer() {
		try {
			await LoadAndStartAllAsync();

			Assert.Equal(5L, FakeThirdBridge.AddValues(2, 3));
		} finally {
			ModuleHostTests.ResetModules();
		}
	}

	[Fact]
	public void TargetNotReady_IsRejectedByReadyGate() {
		ModuleHostTests.ResetModules();
		try {
			ModuleHost.LoadAll();

			var error = Assert.Throws<InvalidOperationException>(() => FakeThirdBridge.DescribeRuntimeState());
			Assert.Contains("FakeModule", error.Message);
		} finally {
			ModuleHostTests.ResetModules();
		}
	}

	[Fact]
	public async Task StopTarget_ClosesBridgeAgain() {
		try {
			await LoadAndStartAllAsync();
			var run = ModuleHost.Find("FakeModule");
			Assert.NotNull(run);
			Assert.StartsWith("FakeModuleRuntime-", FakeThirdBridge.DescribeRuntimeState());

			var stop = await ModuleHost.StopAsync(run!);

			Assert.Equal(ModuleStopOutcome.Clean, stop.Outcome);
			Assert.Throws<InvalidOperationException>(() => FakeThirdBridge.DescribeRuntimeState());
			Assert.Equal(5L, FakeThirdBridge.AddValues(2, 3));
		} finally {
			ModuleHostTests.ResetModules();
		}
	}

	[Fact]
	public async Task ForgotAfterReset_ExportsAreGone() {
		try {
			await LoadAndStartAllAsync();
			Assert.Equal(5L, FakeThirdBridge.AddValues(2, 3));

			ModuleHost.ResetForTest();

			Assert.Throws<KeyNotFoundException>(() => FakeThirdBridge.AddValues(2, 3));
		} finally {
			ModuleHostTests.ResetModules();
		}
	}
}
