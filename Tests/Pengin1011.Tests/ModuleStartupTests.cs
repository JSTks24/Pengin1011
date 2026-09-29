using System.Runtime.Loader;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pengin1011.Core;
using Pengin1011.Core.Modules;
using Pengin1011.Modules.FakeModule;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ModuleStartupTests {
	private static string ModuleDir() {
		return Path.Combine(AppContext.BaseDirectory, "module");
	}

	private static string AddStubDll(string name) {
		var path = Path.Combine(ModuleDir(), name + ".dll");
		File.WriteAllText(path, "不是有效的 PE 文件");
		return path;
	}

	private static async Task AwaitInitAsync(LoadedModule run, int timeoutMs = 5000) {
		if (run.InitTask == null) return;
		try {
			await run.InitTask.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
		} catch (TimeoutException) {
		} catch (Exception) {
		}
		if (!run.InitTask.IsCompleted) {
			ReleaseGate(run, "InitGate");
			try {
				await run.InitTask.WaitAsync(TimeSpan.FromSeconds(5));
			} catch (TimeoutException) {
			} catch (Exception) {
			}
		}
	}

	private static void ReleaseGate(LoadedModule run, string field) {
		var info = ModuleProbe.RuntimeType(run).GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
		(info?.GetValue(null) as TaskCompletionSource)?.TrySetResult();
	}

	private static async Task WaitStateSettledAsync(LoadedModule run, ModuleState expected, int timeoutMs = 15000) {
		var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
		while (run.State != expected && DateTime.UtcNow < deadline) {
			await Task.Delay(25);
		}
		Assert.Equal(expected, run.State);
	}

	private static async Task WaitStopCountAsync(LoadedModule run, int expected, int timeoutMs = 5000) {
		var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
		while (ModuleProbe.RuntimeStaticInt(run, "StopCount") != expected && DateTime.UtcNow < deadline) {
			await Task.Delay(25);
		}
		Assert.Equal(expected, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
	}

	private static async Task<InteractionService> BootAsync() {
		ModuleHostTests.ResetModules();
		InteractionHost.ResetForTest();
		DiscordGateway.Init(new DiscordSocketClient());
		ModuleHost.LoadAll();
		await InteractionHost.PublishInitialAsync();
		foreach (var run in ModuleHost.Modules) {
			await AwaitInitAsync(run);
		}
		return InteractionHost.Current.Service;
	}

	private static async Task TeardownAsync() {
		foreach (var run in ModuleHost.Modules) {
			await ModuleHost.StopAsync(run);
		}
		HostExit.ResetForTest();
		ExitCoordinator.ResetForTest();
		InteractionHost.ResetForTest();
		ModuleHostTests.ResetModules();
		DiscordGateway.DetachForTest();
	}

	private static List<string> Names() {
		return [.. ModuleHost.Modules.Select(module => module.Name).OrderBy(name => name, StringComparer.Ordinal)];
	}

	private static async Task SyncOnlyAsync() {
		InteractionHost.OnGatewayReady();
		var task = InteractionHost.SyncTaskForTest;
		Assert.NotNull(task);
		await task.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task S01_Startup_EachRuntimeInitializedExactlyOnce() {
		await BootAsync();
		try {
			Assert.Equal(3, ModuleHost.Modules.Count);
			foreach (var run in ModuleHost.Modules) {
				Assert.Equal(ModuleState.Ready, run.State);
				Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
				Assert.Equal(0, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
			}
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task S02_RepeatedStartupEntry_NoSecondServiceOrInit() {
		var first = await BootAsync();
		var snapshot = InteractionHost.SnapshotForTest;
		var retiredBefore = InteractionHost.RetiredServiceCountForTest;
		var createdBefore = InteractionHost.CreatedServiceCountForTest;
		try {
			await InteractionHost.PublishInitialAsync();
			foreach (var run in ModuleHost.Modules) {
				Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
			}
			Assert.Same(first, InteractionHost.Current.Service);
			Assert.Same(snapshot, InteractionHost.SnapshotForTest);
			Assert.Same(snapshot!.Modules, InteractionHost.Current.Modules);
			Assert.Equal(3, InteractionHost.Current.Modules.Count);
			Assert.Equal(retiredBefore, InteractionHost.RetiredServiceCountForTest);
			Assert.Equal(createdBefore, InteractionHost.CreatedServiceCountForTest);
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task S02b_ConcurrentStartupEntry_SingleServiceSingleInitPerRuntime() {
		ModuleHostTests.ResetModules();
		InteractionHost.ResetForTest();
		DiscordGateway.Init(new DiscordSocketClient());
		ModuleHost.LoadAll();
		var retiredBefore = InteractionHost.RetiredServiceCountForTest;
		var createdBefore = InteractionHost.CreatedServiceCountForTest;
		var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		try {
			var callers = Enumerable.Range(0, 6).Select(_ => Task.Run(async () => {
				await start.Task;
				await InteractionHost.PublishInitialAsync();
			})).ToList();
			start.SetResult();
			await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(15));

			Assert.Equal(createdBefore + 1, InteractionHost.CreatedServiceCountForTest);
			Assert.Equal(retiredBefore, InteractionHost.RetiredServiceCountForTest);
			Assert.Equal(3, InteractionHost.Current.Modules.Count);
			foreach (var run in ModuleHost.Modules) {
				Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
			}
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task S02c_ZeroModules_RepeatedPublish_KeepsSingleService() {
		ModuleHostTests.ResetModules();
		InteractionHost.ResetForTest();
		var baseDir = Path.Combine(Path.GetTempPath(), $"qq1011_zero_{Guid.NewGuid():N}");
		Directory.CreateDirectory(baseDir);
		ModuleHost.ModuleDirectoryOverrideForTest = baseDir;
		DiscordGateway.Init(new DiscordSocketClient());
		ModuleHost.LoadAll();
		var createdBefore = InteractionHost.CreatedServiceCountForTest;
		try {
			await InteractionHost.PublishInitialAsync();
			var service = InteractionHost.Current.Service;
			Assert.Empty(InteractionHost.Current.Modules);

			await InteractionHost.PublishInitialAsync();
			Assert.Same(service, InteractionHost.Current.Service);
			Assert.Empty(InteractionHost.Current.Modules);
			Assert.Equal(createdBefore + 1, InteractionHost.CreatedServiceCountForTest);
		} finally {
			Directory.Delete(baseDir, true);
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task S03_ModuleAddedAfterStartup_NeverInstalled() {
		await BootAsync();
		var added = AddStubDll("FakeLate");
		try {
			var before = Names();
			ModuleHost.LoadAll();
			await InteractionHost.SyncAsync(InteractionHost.ShutdownToken);
			await SyncOnlyAsync();

			Assert.Equal(before, Names());
			Assert.DoesNotContain("FakeLate", Names());
			foreach (var run in ModuleHost.Modules) {
				Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
			}
		} finally {
			File.Delete(added);
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task S04_SyncAndReady_DoNotReloadOrReinitialize() {
		await BootAsync();
		try {
			await InteractionHost.SyncAsync(InteractionHost.ShutdownToken);
			await SyncOnlyAsync();
			await SyncOnlyAsync();

			foreach (var run in ModuleHost.Modules) {
				Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
				Assert.Equal(0, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
			}
			Assert.Equal(3, ModuleHost.Modules.Count);
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public void S05_StartupScan_SeesCurrentDirectoryOnly() {
		ModuleHostTests.ResetModules();
		var added = AddStubDll("FakeFresh");
		try {
			ModuleHost.LoadAll();
			Assert.DoesNotContain("FakeFresh", ModuleHost.Modules.Select(module => module.Name));
			Assert.Contains("FakeModule", ModuleHost.Modules.Select(module => module.Name));
		} finally {
			File.Delete(added);
			ModuleHostTests.ResetModules();
		}
	}

	[Fact]
	public void S06_ModuleDirectoryLoad_KeepsOneAssemblyPerFile() {
		ModuleHostTests.ResetModules();
		var baseDir = Path.Combine(Path.GetTempPath(), $"qq1011_alc_{Guid.NewGuid():N}");
		Directory.CreateDirectory(baseDir);
		File.Copy(Path.Combine(ModuleDir(), "FakeModule.dll"), Path.Combine(baseDir, "FakeModule.dll"));
		ModuleHost.ModuleDirectoryOverrideForTest = baseDir;
		try {
			ModuleHost.LoadAll();
			var before = ModuleHost.Modules.Single(module => module.Name == "FakeModule");
			var again = ModuleHost.LoadOne("FakeModule", before.FilePath);
			Assert.Same(before.Assembly, again.Assembly);
			Assert.Same(before.Runtime.GetType(), again.Runtime.GetType());
			Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(before.Assembly));
			Assert.False(Directory.Exists(Path.Combine(baseDir, "runtime", "module-cache")));
		} finally {
			ModuleHostTests.ResetModules();
			Directory.Delete(baseDir, true);
		}
	}

	[Fact]
	public void S07_ZeroModules_EmptyDirectoryScannedOnce() {
		ModuleHostTests.ResetModules();
		var baseDir = Path.Combine(Path.GetTempPath(), $"qq1011_zero_{Guid.NewGuid():N}");
		Directory.CreateDirectory(baseDir);
		ModuleHost.ModuleDirectoryOverrideForTest = baseDir;
		try {
			ModuleHost.LoadAll();
			Assert.Empty(ModuleHost.Modules);

			File.WriteAllText(Path.Combine(baseDir, "Late.dll"), "不是有效的 PE 文件");
			ModuleHost.LoadAll();
			Assert.Empty(ModuleHost.Modules);
		} finally {
			ModuleHostTests.ResetModules();
			Directory.Delete(baseDir, true);
		}
	}

	[Fact]
	public async Task S08_BrokenAndMissingContract_OthersKeepServing() {
		ModuleHostTests.ResetModules();
		var broken = AddStubDll("FakeBroken");
		var bare = Path.Combine(ModuleDir(), "FakeBare.dll");
		File.Copy(Path.Combine(AppContext.BaseDirectory, "baremodule", "FakeBare.dll"), bare, true);
		try {
			var service = await BootAsync();
			var bareRun = ModuleHost.Modules.Single(module => module.Name == "FakeBare");
			Assert.Equal(ModuleState.Disabled, bareRun.State);
			Assert.Null(bareRun.InitTask);
			Assert.NotNull(bareRun.DisabledReason);
			Assert.DoesNotContain(ModuleHost.Modules, module => module.Name == "FakeBroken");
			Assert.Contains(service.SlashCommands, command => command.Name == "fakeping");

			var fake = ModuleHost.Modules.Single(module => module.Name == "FakeModule");
			Assert.Equal(ModuleState.Ready, fake.State);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(fake, "InitCount"));
		} finally {
			File.Delete(broken);
			File.Delete(bare);
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task S09_InitFailure_DisabledWithSingleCleanup_LegalModulesServe() {
		await BootAsync();
		await TeardownAsync();

		ModuleHostTests.ResetModules();
		DiscordGateway.Init(new DiscordSocketClient());
		ModuleHost.LoadAll();
		var failing = ModuleHost.Modules.Single(module => module.Name == "FakeSecond");
		ModuleProbe.SetRuntimeStatic(failing, "OnInitFailure", (Func<Exception>)(() => new InvalidOperationException("启动初始化失败")));
		try {
			await InteractionHost.PublishInitialAsync();
			var failed = ModuleHost.Modules.Single(module => module.Name == "FakeSecond");
			var healthy = ModuleHost.Modules.Single(module => module.Name == "FakeModule");
			await AwaitInitAsync(failed);
			await WaitStateSettledAsync(failed, ModuleState.Disabled);
			await WaitStateSettledAsync(healthy, ModuleState.Ready);
			await WaitStopCountAsync(failed, 1);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(failed, "InitCount"));
			Assert.Equal(ModuleState.Ready, healthy.State);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(healthy, "InitCount"));
			Assert.Contains(InteractionHost.Current.Modules, run => run.Name == "FakeModule");
			Assert.True(failed.State is ModuleState.Disabled or ModuleState.Stopped, $"失败模块状态={failed.State}");
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task S10_NormalExit_SingleCleanupPerRuntime() {
		await BootAsync();
		try {
			var report = await HostExit.StopAsync();

			Assert.True(report.Success, $"failures: {string.Join(";", report.Failures)}");
			foreach (var run in ModuleHost.Modules) {
				Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
				Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
				Assert.NotNull(run.FinalStopResult);
			}
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task S11_RestartPersistence_TwoIndependentProcesses() {
		var baseDir = Path.Combine(Path.GetTempPath(), $"qq1011_restart_{Guid.NewGuid():N}");
		Directory.CreateDirectory(baseDir);
		File.WriteAllText(Path.Combine(baseDir, "config.json"), """
			{
			  "Discord": { "Token": "test-token" },
			  "AI": {
			    "Provider": "openai",
			    "MaxParallel": 2,
			    "OpenAI": { "ApiKey": "test-key", "BaseUrl": "http://127.0.0.1:1/v1", "Model": "test-model" },
			    "Gemini": { "ApiKey": "", "Project": "", "Location": "", "Model": "" }
			  }
			}
			""");
		try {
			await TestChildProcess.RunAsync($"startup-persist-write {baseDir}");
			Assert.True(File.Exists(Path.Combine(baseDir, "data", "FakeModule.db")), "进程 A 未按迁移建档");
			await TestChildProcess.RunAsync($"startup-persist-verify {baseDir}");
		} finally {
			Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
			Directory.Delete(baseDir, true);
		}
	}
}
