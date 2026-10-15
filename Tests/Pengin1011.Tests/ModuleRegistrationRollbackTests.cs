using Pengin1011.Core;
using Pengin1011.Core.Modules;
using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ModuleRegistrationRollbackTests {
	private sealed class StubRuntime : IModuleRuntime {
		public Task InitializeAsync(CancellationToken ct) {
			return Task.CompletedTask;
		}

		public Task StopAsync(CancellationToken ct) {
			return Task.CompletedTask;
		}
	}

	private static LoadedModule MakeRun(string name) {
		return new LoadedModule {
			Name = name,
			FilePath = "",
			Assembly = typeof(ModuleRegistrationRollbackTests).Assembly,
			LoadedAt = DateTimeOffset.Now,
			Runtime = new StubRuntime(),
		};
	}

	private static void Reset() {
		ModuleBridgeDispatcher.ForgetAll();
		AIPresetUsageRegistry.ForgetAll();
	}

	[Fact]
	public void M01_FailedRunRollsBackEverythingAndLeavesHealthyRunUntouched() {
		Reset();
		try {
			var healthyUsages = AIPresetUsageRegistry.CreateBatchForTest("Healthy", ("Caller#Go", "prod"));
			AIPresetUsageRegistry.Commit(healthyUsages);
			var healthy = MakeRun("Healthy");
			new ModuleBridgeRegistrar(healthy, []).Add("Ping", (Func<string>)(() => "pong"));
			healthy.State = ModuleState.Ready;

			using (var failing = new ModuleRegistration("Failing")) {
				var failedUsages = AIPresetUsageRegistry.CreateBatchForTest("Failing", ("Bad#Go", "absent"));
				failing.Track(() => AIPresetUsageRegistry.Rollback(failedUsages));
				AIPresetUsageRegistry.Commit(failedUsages);

				var badRun = MakeRun("Failing");
				new ModuleBridgeRegistrar(badRun, []).Add("Boom", (Func<string>)(() => throw new InvalidOperationException("坏模块")));
				failing.Track(() => ModuleBridgeDispatcher.RemoveModule(badRun.Name));
			}

			Assert.DoesNotContain(AIPresetUsageRegistry.Usages, usage => usage.Owner == "Failing");
			Assert.Contains(AIPresetUsageRegistry.Usages, usage => usage.Owner == "Healthy");
			var call = ModuleBridgeDispatcher.Admit<Func<string>>("Healthy", "Ping");
			using (call.Lease) {
				Assert.Equal("pong", call.Handler());
			}
			Assert.Throws<KeyNotFoundException>(() => ModuleBridgeDispatcher.Admit<Func<string>>("Failing", "Boom"));
		} finally {
			Reset();
		}
	}

	[Fact]
	public void M02_RollbackDoesNotTouchOtherRuns() {
		Reset();
		try {
			AIPresetUsageRegistry.Commit(AIPresetUsageRegistry.CreateBatchForTest("OtherRun", ("A#One", "prod")));
			var before = AIPresetUsageRegistry.Usages.Count;

			using (var registration = new ModuleRegistration("RolledBack")) {
				var batch = AIPresetUsageRegistry.CreateBatchForTest("RolledBack", ("B#Two", "absent"));
				registration.Track(() => AIPresetUsageRegistry.Rollback(batch));
				AIPresetUsageRegistry.Commit(batch);
			}

			Assert.Equal(before, AIPresetUsageRegistry.Usages.Count);
			Assert.DoesNotContain(AIPresetUsageRegistry.Usages, usage => usage.Owner == "RolledBack");
			Assert.Contains(AIPresetUsageRegistry.Usages, usage => usage.Owner == "OtherRun");
		} finally {
			Reset();
		}
	}

	[Fact]
	public void M02_RollbackAlsoRevokesBridgeExportsOfTheFailedRun() {
		Reset();
		try {
			using (var registration = new ModuleRegistration("BridgeFail")) {
				var batch = AIPresetUsageRegistry.CreateBatchForTest("BridgeFail", ("C#Three", "prod"));
				registration.Track(() => AIPresetUsageRegistry.Rollback(batch));
				AIPresetUsageRegistry.Commit(batch);
				var run = MakeRun("BridgeFail");
				new ModuleBridgeRegistrar(run, []).Add("Half", (Func<string>)(() => "half"));
				registration.Track(() => ModuleBridgeDispatcher.RemoveModule(run.Name));
			}

			Assert.Empty(AIPresetUsageRegistry.Usages);
			Assert.Throws<KeyNotFoundException>(() => ModuleBridgeDispatcher.Admit<Func<string>>("BridgeFail", "Half"));
		} finally {
			Reset();
		}
	}

	[Fact]
	public void CommittedRegistration_IsNotRolledBackByDispose() {
		Reset();
		try {
			using var registration = new ModuleRegistration("Committed");
			var batch = AIPresetUsageRegistry.CreateBatchForTest("Committed", ("D#Four", "prod"));
			registration.Track(() => AIPresetUsageRegistry.Rollback(batch));
			AIPresetUsageRegistry.Commit(batch);
			registration.Commit();

			Assert.Single(AIPresetUsageRegistry.Usages);

			registration.Dispose();

			Assert.Single(AIPresetUsageRegistry.Usages);
		} finally {
			Reset();
		}
	}
}
