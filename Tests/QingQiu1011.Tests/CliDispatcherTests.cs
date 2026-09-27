using QingQiu1011;
using QingQiu1011.Core;

namespace QingQiu1011.Tests;

[Collection("BaseDirSerial")]
public sealed class CliDispatcherTests {
	[Fact]
	public async Task Help_ListsCommands() {
		var result = await CliDispatcher.ExecuteAsync("help");
		Assert.DoesNotContain("reload", result.Output);
		Assert.Contains("db backup", result.Output);
		Assert.Contains("sync", result.Output);
		Assert.False(result.ShouldExit);
	}

	[Fact]
	public async Task Status_ShowsGatewayAndCounters() {
		var result = await CliDispatcher.ExecuteAsync("status");
		Assert.Contains("网关状态", result.Output);
		Assert.Contains("模块", result.Output);
	}

	[Fact]
	public async Task Modules_WithNoneLoaded() {
		ModuleHost.ResetForTest();
		var result = await CliDispatcher.ExecuteAsync("modules");
		Assert.Contains("没有已加载的模块", result.Output);
	}

	[Fact]
	public async Task Reload_RemovedCommand_TellsToRestartWithoutTouchingWorld() {
		ModuleHostTests.ResetModules();
		var added = Path.Combine(AppContext.BaseDirectory, "module", "FakeAdded.dll");
		File.WriteAllText(added, "不是有效的 PE 文件");
		try {
			ModuleHost.LoadAll();
			var before = ModuleHost.Modules.Select(module => module.Name).ToList();
			var result = await CliDispatcher.ExecuteAsync("reload all");
			Assert.Contains("热重载已移除", result.Output);
			Assert.Contains("启动", result.Output);
			Assert.False(result.ShouldExit);
			Assert.Equal(before, ModuleHost.Modules.Select(module => module.Name));
			Assert.DoesNotContain(ModuleHost.Modules, module => module.Name == "FakeAdded");
		} finally {
			File.Delete(added);
			ModuleHostTests.ResetModules();
		}
	}

	[Fact]
	public async Task Db_WithoutSubcommand_UsageHint() {
		var result = await CliDispatcher.ExecuteAsync("db");
		Assert.Contains("用法", result.Output);
	}

	[Fact]
	public async Task UnknownCommand_Hinted() {
		var result = await CliDispatcher.ExecuteAsync("foobar");
		Assert.Contains("未知命令", result.Output);
	}

	[Fact]
	public async Task Exit_RequestsShutdown() {
		var result = await CliDispatcher.ExecuteAsync("exit");
		Assert.True(result.ShouldExit);
	}

	[Fact]
	public async Task QuitAlias_RequestsShutdown() {
		var result = await CliDispatcher.ExecuteAsync("quit");
		Assert.True(result.ShouldExit);
	}

	[Fact]
	public async Task EmptyLine_Ignored() {
		var result = await CliDispatcher.ExecuteAsync("   ");
		Assert.Equal("", result.Output);
		Assert.False(result.ShouldExit);
	}
}
