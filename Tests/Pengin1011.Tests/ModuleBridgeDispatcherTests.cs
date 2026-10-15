using Pengin1011.Core;
using Pengin1011.Core.Modules;
using Pengin1011.Modules.FakeSecond;

namespace Pengin1011.Tests;

public sealed class ModuleBridgeDispatcherTests {
	private static LoadedModule MakeRun(string name, ModuleState state = ModuleState.Ready) {
		return new LoadedModule {
			Name = name,
			FilePath = "",
			Assembly = typeof(ModuleBridgeDispatcherTests).Assembly,
			LoadedAt = DateTimeOffset.Now,
			Runtime = new FakeSecondRuntime(),
			State = state,
		};
	}

	private static ModuleBridgeRegistrar Registrar(string module, ModuleState state = ModuleState.Ready) {
		return new ModuleBridgeRegistrar(MakeRun(module, state), []);
	}

	[Fact]
	public void Admit_DisposingTwice_ReleasesExactlyOneCall() {
		try {
			var run = MakeRun("disp-dispose");
			new ModuleBridgeRegistrar(run, []).Add("Ping", (Func<string>)(() => "pong"));
			var call = ModuleBridgeDispatcher.Admit<Func<string>>(run.Name, "Ping");

			Assert.Equal(1, ModuleBridgeDispatcher.ActiveCallsOf(run));
			call.Lease.Dispose();
			call.Lease.Dispose();
			Assert.Equal(0, ModuleBridgeDispatcher.ActiveCallsOf(run));
		} finally {
			ModuleHost.ResetForTest();
		}
	}

	[Fact]
	public void Admit_ReadyModule_ReturnsRegisteredDelegate() {
		try {
			Registrar("disp-ready").Add("Add", (Func<int, int, long>)((a, b) => a + b));

			var call = ModuleBridgeDispatcher.Admit<Func<int, int, long>>("disp-ready", "Add");
			using (call.Lease) {
				Assert.Equal(7L, call.Handler(3, 4));
			}
		} finally {
			ModuleHost.ResetForTest();
		}
	}

	[Fact]
	public void Admit_NotReadyModule_ThrowsInvalidOperation() {
		try {
			Registrar("disp-notready", ModuleState.Starting).Add("Ping", (Func<string>)(() => "pong"));

			var error = Assert.Throws<InvalidOperationException>(() => ModuleBridgeDispatcher.Admit<Func<string>>("disp-notready", "Ping"));
			Assert.Contains("disp-notready", error.Message);
		} finally {
			ModuleHost.ResetForTest();
		}
	}

	[Fact]
	public void Admit_NotReadyModule_SucceedsAfterBecomingReady() {
		try {
			var run = MakeRun("disp-transition", ModuleState.Stopping);
			new ModuleBridgeRegistrar(run, []).Add("Ping", (Func<string>)(() => "pong"));
			Assert.Throws<InvalidOperationException>(() => ModuleBridgeDispatcher.Admit<Func<string>>("disp-transition", "Ping"));

			run.State = ModuleState.Ready;

			var call = ModuleBridgeDispatcher.Admit<Func<string>>("disp-transition", "Ping");
			using (call.Lease) {
				Assert.Equal("pong", call.Handler());
			}
		} finally {
			ModuleHost.ResetForTest();
		}
	}

	[Fact]
	public void Admit_UnknownExport_ThrowsKeyNotFound() {
		try {
			Registrar("disp-unknown").Add("Known", (Func<string>)(() => "x"));

			var error = Assert.Throws<KeyNotFoundException>(() => ModuleBridgeDispatcher.Admit<Func<string>>("disp-unknown", "Missing"));
			Assert.Contains("disp-unknown", error.Message);
			Assert.Contains("Missing", error.Message);
		} finally {
			ModuleHost.ResetForTest();
		}
	}

	[Fact]
	public void Admit_UnknownModule_ThrowsKeyNotFound() {
		try {
			Assert.Throws<KeyNotFoundException>(() => ModuleBridgeDispatcher.Admit<Func<string>>("disp-nomodule", "Any"));
		} finally {
			ModuleHost.ResetForTest();
		}
	}

	[Fact]
	public void Admit_SignatureMismatch_ThrowsWithBothTypes() {
		try {
			Registrar("disp-signature").Add("Value", (Func<int, int>)(value => value + 1));

			var error = Assert.Throws<InvalidOperationException>(() => ModuleBridgeDispatcher.Admit<Func<string, string>>("disp-signature", "Value"));
			Assert.Contains("disp-signature", error.Message);
			Assert.Contains("Value", error.Message);
			Assert.Contains("Func", error.Message);
			Assert.Contains("System.String", error.Message);
			Assert.Contains("System.Int32", error.Message);
		} finally {
			ModuleHost.ResetForTest();
		}
	}

	[Fact]
	public void Admit_SignatureMismatch_DistinguishesDifferentGenericArguments() {
		try {
			Registrar("disp-generic-signature").Add("Echo", (Func<int, int>)(value => value));

			var error = Assert.Throws<InvalidOperationException>(() => ModuleBridgeDispatcher.Admit<Func<string, string>>("disp-generic-signature", "Echo"));

			Assert.Contains("Int32", error.Message);
			Assert.Contains("String", error.Message);
		} finally {
			ModuleHost.ResetForTest();
		}
	}

	[Fact]
	public void AddExport_DuplicateName_ThrowsInvalidOperation() {
		try {
			var registrar = Registrar("disp-duplicate");
			registrar.Add("Same", (Func<string>)(() => "first"));

			Assert.Throws<InvalidOperationException>(() => registrar.Add("Same", (Func<string>)(() => "second")));
		} finally {
			ModuleHost.ResetForTest();
		}
	}

	[Fact]
	public void AddExport_SameNameInDifferentModules_IsAllowed() {
		try {
			Registrar("disp-mod-a").Add("Shared", (Func<string>)(() => "a"));
			Registrar("disp-mod-b").Add("Shared", (Func<string>)(() => "b"));

			var callA = ModuleBridgeDispatcher.Admit<Func<string>>("disp-mod-a", "Shared");
			using (callA.Lease) {
				Assert.Equal("a", callA.Handler());
			}
			var callB = ModuleBridgeDispatcher.Admit<Func<string>>("disp-mod-b", "Shared");
			using (callB.Lease) {
				Assert.Equal("b", callB.Handler());
			}
		} finally {
			ModuleHost.ResetForTest();
		}
	}

	[Fact]
	public void RegisterModule_WithoutProviderAttribute_IsNoOp() {
		try {
			var run = MakeRun("disp-noprovider");

			ModuleBridgeDispatcher.RegisterModule(run.Assembly, run);

			Assert.Throws<KeyNotFoundException>(() => ModuleBridgeDispatcher.Admit<Func<string>>("disp-noprovider", "Any"));
		} finally {
			ModuleHost.ResetForTest();
		}
	}

	[Fact]
	public void RegisterModule_CalledTwice_ReplacesExports() {
		try {
			var run = ModuleHost.LoadOne("FakeModule", ModuleHostTests.ModuleDll("FakeModule"));
			run.State = ModuleState.Ready;

			ModuleBridgeDispatcher.RegisterModule(run.Assembly, run);
			ModuleBridgeDispatcher.RegisterModule(run.Assembly, run);

			var call = ModuleBridgeDispatcher.Admit<Func<string>>("FakeModule", "DescribeState");
			using (call.Lease) {
				Assert.NotNull(call.Handler);
			}
		} finally {
			ModuleHostTests.ResetModules();
		}
	}

	[Fact]
	public async Task Admit_ConcurrentCalls_AllSucceed() {
		try {
			Registrar("disp-concurrent").Add("Echo", (Func<int, int>)(value => value * 2));

			var tasks = Enumerable.Range(0, 64).Select(index => Task.Run(() => {
				var call = ModuleBridgeDispatcher.Admit<Func<int, int>>("disp-concurrent", "Echo");
				using (call.Lease) {
					return call.Handler(index);
				}
			})).ToArray();
			var results = await Task.WhenAll(tasks);

			Assert.Equal(Enumerable.Range(0, 64).Select(index => index * 2), results);
		} finally {
			ModuleHost.ResetForTest();
		}
	}
}
