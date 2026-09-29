using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Pengin1011.Core;
using Pengin1011.Core.Modules;
using Pengin1011.Services.Discord;

using Pengin1011.Modules.FakeModule;
using Pengin1011.Modules.FakeSecond;
using Pengin1011.Modules.FakeThird;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ModuleHostTests {
	private static string TempBase() {
		return Path.Combine(Path.GetTempPath(), $"qq1011_mod_{Guid.NewGuid():N}");
	}

	private static void Cleanup(string baseDir) {
		try {
			Directory.Delete(baseDir, true);
		} catch (IOException) {
			Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
			Directory.Delete(baseDir, true);
		}
	}

	internal static void ResetModules() {
		ModuleHost.ResetForTest();
		ModuleHost.ModuleDirectoryOverrideForTest = null;
		FakeModuleRuntime.Reset();
		FakeModule.ResetCommands();
		FakeSecondRuntime.Reset();
		FakeThirdRuntime.Reset();
		FakeThird.Reset();
	}

	internal static string ModuleDll(string name) {
		var path = Path.Combine(AppContext.BaseDirectory, "module", name + ".dll");
		Assert.True(File.Exists(path), $"{name}.dll 缺失：测试资产应由构建复制保障");
		return path;
	}

	[Fact]
	public async Task LoadAttachStartStop_FullCycle() {
		ResetModules();
		var interactions = NewInteractionService();
		try {
			ModuleHost.LoadAll();
			Assert.Contains(ModuleHost.Modules, module => module.Name == "FakeModule");

			var attach = await InteractionHost.AttachAsync(interactions, ModuleHost.Modules);
			Assert.Empty(attach.Errors);

			foreach (var run in ModuleHost.Modules) {
				ModuleHost.StartRuntime(run);
			}
			var fakeRun = ModuleHost.Modules.Single(module => module.Name == "FakeModule");
			await fakeRun.InitTask!;
			await WaitStateAsync(fakeRun, ModuleState.Ready);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(fakeRun, "InitCount"));

			var stop = await ModuleHost.StopAsync(fakeRun);
			Assert.Equal(ModuleStopOutcome.Clean, stop.Outcome);
			Assert.Equal(ModuleState.Stopped, fakeRun.State);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(fakeRun, "StopCount"));
			Assert.Contains(fakeRun, ModuleHost.Modules);
			Assert.Same(fakeRun, ModuleRegistry.RunOf(fakeRun.Assembly));
		} finally {
			ResetModules();
		}
	}

	[Fact]
	public async Task StopThenReopenDbContext_SameProcessKeepsData() {
		ResetModules();
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(baseDir);
		try {
			ModuleHost.LoadAll();
			var assembly = ModuleHost.Modules.Single(module => module.Name == "FakeModule").Assembly;
			var contextType = assembly.GetType("Pengin1011.Modules.FakeModule.FakeModuleDbContext")!;
			var itemType = assembly.GetType("Pengin1011.Modules.FakeModule.FakeItem")!;
			var open = typeof(Databases).GetMethod("Open")!.MakeGenericMethod(contextType);
			await using (var db = (DbContext)open.Invoke(null, null)!) {
				var item = Activator.CreateInstance(itemType)!;
				itemType.GetProperty("Name")!.SetValue(item, "重启前");
				db.Add(item);
				await db.SaveChangesAsync();
			}

			var fakeRun = ModuleHost.Modules.Single(module => module.Name == "FakeModule");
			var stop = await ModuleHost.StopAsync(fakeRun);
			Assert.Equal(ModuleStopOutcome.Clean, stop.Outcome);

			await using (var db = (DbContext)open.Invoke(null, null)!) {
				var names = await db.Database.SqlQuery<string>($"SELECT \"Name\" AS \"Value\" FROM \"Items\"").ToListAsync();
				Assert.Equal("重启前", names.Single());
			}
		} finally {
			foreach (var run in ModuleHost.Modules) {
				await ModuleHost.StopAsync(run);
			}
			ResetModules();
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public void LoadAll_SkipsCorruptDll() {
		ResetModules();
		var moduleDir = Path.Combine(AppContext.BaseDirectory, "module");
		Directory.CreateDirectory(moduleDir);
		var badPath = Path.Combine(moduleDir, "Bad.dll");
		File.WriteAllText(badPath, "这不是有效的 PE 文件");
		try {
			ModuleHost.LoadAll();
			Assert.DoesNotContain(ModuleHost.Modules, module => module.Name == "Bad");
			Assert.Contains(ModuleHost.Modules, module => module.Name == "FakeModule");
		} finally {
			File.Delete(badPath);
			ResetModules();
		}
	}

	[Fact]
	public async Task AttachAsync_DefaultRunModeSync_AppliesToBuiltCommands() {
		ResetModules();
		var interactions = NewInteractionService();
		try {
			ModuleHost.LoadAll();
			var attach = await InteractionHost.AttachAsync(interactions, ModuleHost.Modules);
			Assert.DoesNotContain(attach.Errors, error => error.Contains("RunMode"));
		} finally {
			ResetModules();
		}
	}

	[Fact]
	public async Task AttachAsync_MissingAvailabilityAttribute_ReportsContractError() {
		ResetModules();
		var barePath = Path.Combine(AppContext.BaseDirectory, "baremodule", "FakeBare.dll");
		Assert.True(File.Exists(barePath), "FakeBare.dll 缺失：测试资产应由构建复制保障");
		var interactions = NewInteractionService();
		var run = ModuleHost.LoadOne("FakeBare", barePath);
		try {
			var attach = await InteractionHost.AttachAsync(interactions, [run]);
			Assert.Contains(attach.Errors, error => error.Contains("ModuleAvailability"));
			Assert.Equal(run, Assert.Single(attach.Invalid));
		} finally {
			ModuleHost.ResetForTest();
		}
	}

	[Fact]
	public void Adopt_SameAssemblyRegisteredTwice_Rejected() {
		ResetModules();
		var first = ModuleHost.LoadOne("AliasFirst", ModuleDll("FakeModule"));
		var second = ModuleHost.LoadOne("AliasSecond", ModuleDll("FakeModule"));
		try {
			ModuleHost.Adopt(first);
			Assert.Throws<InvalidOperationException>(() => ModuleHost.Adopt(second));
			Assert.Single(ModuleHost.Modules);
			Assert.Same(first, ModuleHost.Modules.Single());
			Assert.Same(first, ModuleRegistry.RunOf(first.Assembly));
		} finally {
			ResetModules();
		}
	}

	[Fact]
	public void Adopt_RepeatedStartupEntry_PublishesOneRecordPerModule() {
		ResetModules();
		try {
			ModuleHost.LoadAll();
			ModuleHost.LoadAll();
			Assert.Single(ModuleHost.Modules, module => module.Name == "FakeModule");
			Assert.Equal(3, ModuleHost.Modules.Count);
		} finally {
			ResetModules();
		}
	}

	[Fact]
	public void LoadAll_AfterScan_IgnoresModuleAddedDuringProcess() {
		ResetModules();
		var copied = Path.Combine(AppContext.BaseDirectory, "module", "FakeBare.dll");
		try {
			ModuleHost.LoadAll();
			File.Copy(Path.Combine(AppContext.BaseDirectory, "baremodule", "FakeBare.dll"), copied, true);
			ModuleHost.LoadAll();
			Assert.DoesNotContain(ModuleHost.Modules, module => module.Name == "FakeBare");
		} finally {
			File.Delete(copied);
			ResetModules();
		}
	}

	internal static async Task WaitStateAsync(LoadedModule run, ModuleState expected, int timeoutMs = 5000) {
		var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
		while (run.State != expected && DateTime.UtcNow < deadline) {
			await Task.Delay(25);
		}
		Assert.Equal(expected, run.State);
	}

	private static InteractionService NewInteractionService() {
		var client = new DiscordSocketClient();
		return new InteractionService(client.Rest, new InteractionServiceConfig { AutoServiceScopes = false, DefaultRunMode = RunMode.Sync, LogLevel = Discord.LogSeverity.Warning });
	}
}
