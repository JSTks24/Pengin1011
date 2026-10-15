using Pengin1011.Core;
using Pengin1011.Core.Modules;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ModuleBridgeBadProviderTests {
	private static string TempDir() {
		var dir = Path.Combine(Path.GetTempPath(), $"qq1011_badbridge_{Guid.NewGuid():N}");
		Directory.CreateDirectory(dir);
		return dir;
	}

	private static void Remove(string dir) {
		try {
			Directory.Delete(dir, true);
		} catch (IOException) {
			Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
			Directory.Delete(dir, true);
		}
	}

	[Fact]
	public void LoadAll_BadProvider_RejectsOnlyThatModule() {
		ModuleHostTests.ResetModules();
		var previousDirectory = ModuleHost.ModuleDirectoryOverrideForTest;
		var dir = TempDir();
		try {
			File.Copy(Path.Combine(AppContext.BaseDirectory, "badbridge", "FakeBridgeBad.dll"), Path.Combine(dir, "FakeBridgeBad.dll"), true);
			File.Copy(ModuleHostTests.ModuleDll("FakeModule"), Path.Combine(dir, "FakeModule.dll"), true);
			ModuleHost.ModuleDirectoryOverrideForTest = dir;

			ModuleHost.LoadAll();

			Assert.DoesNotContain(ModuleHost.Modules, module => module.Name == "FakeBridgeBad");
			Assert.NotNull(ModuleHost.Find("FakeModule"));
		} finally {
			ModuleHost.ModuleDirectoryOverrideForTest = previousDirectory;
			ModuleHostTests.ResetModules();
			Remove(dir);
		}
	}

	[Fact]
	public void LoadOne_BadProvider_ThrowsAndLeavesNoExports() {
		ModuleHostTests.ResetModules();
		try {
			var path = Path.Combine(AppContext.BaseDirectory, "badbridge", "FakeBridgeBad.dll");
			Assert.True(File.Exists(path), "FakeBridgeBad.dll 缺失：测试资产应由构建复制保障");

			Assert.Throws<InvalidOperationException>(() => ModuleHost.LoadOne("FakeBridgeBad", path));
			Assert.Throws<KeyNotFoundException>(() => ModuleBridgeDispatcher.Admit<Func<string>>("FakeBridgeBad", "Anything"));
		} finally {
			ModuleHostTests.ResetModules();
		}
	}

	[Fact]
	public void LoadOne_BadProvider_RevokesTheExportItRegisteredBeforeThrowing() {
		ModuleHostTests.ResetModules();
		try {
			var path = Path.Combine(AppContext.BaseDirectory, "badbridge", "FakeBridgeBad.dll");

			Assert.Throws<InvalidOperationException>(() => ModuleHost.LoadOne("FakeBridgeBad", path));

			Assert.Throws<KeyNotFoundException>(() => ModuleBridgeDispatcher.Admit<Func<string>>("FakeBridgeBad", "HalfRegistered"));
		} finally {
			ModuleHostTests.ResetModules();
		}
	}

	[Fact]
	public void LoadAll_BadProvider_KeepsTheOtherModulesCallable() {
		ModuleHostTests.ResetModules();
		var previousDirectory = ModuleHost.ModuleDirectoryOverrideForTest;
		var dir = TempDir();
		try {
			File.Copy(Path.Combine(AppContext.BaseDirectory, "badbridge", "FakeBridgeBad.dll"), Path.Combine(dir, "FakeBridgeBad.dll"), true);
			File.Copy(ModuleHostTests.ModuleDll("FakeModule"), Path.Combine(dir, "FakeModule.dll"), true);
			ModuleHost.ModuleDirectoryOverrideForTest = dir;

			ModuleHost.LoadAll();

			var survivor = ModuleHost.Find("FakeModule");
			Assert.NotNull(survivor);
			survivor!.State = ModuleState.Ready;
			var call = ModuleBridgeDispatcher.Admit<Func<string>>("FakeModule", "DescribeState");
			using (call.Lease) {
				Assert.NotNull(call.Handler);
			}
			Assert.Throws<KeyNotFoundException>(() => ModuleBridgeDispatcher.Admit<Func<string>>("FakeBridgeBad", "HalfRegistered"));
		} finally {
			ModuleHost.ModuleDirectoryOverrideForTest = previousDirectory;
			ModuleHostTests.ResetModules();
			Remove(dir);
		}
	}
}
