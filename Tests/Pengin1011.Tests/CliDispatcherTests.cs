using Pengin1011.Core.Logging;
using System.Globalization;
using Pengin1011;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class CliDispatcherTests {
	private sealed class CancelOnRead(string input, CancellationTokenSource cancellation) : StringReader(input) {
		public override string? ReadLine() {
			cancellation.Cancel();
			return base.ReadLine();
		}
	}

	[Fact]
	public async Task DiscardedInputLogsOnlyTheCommandName() {
		var path = Path.Combine(Path.GetTempPath(), $"pengin1011_cli_{Guid.NewGuid():N}.log");
		var previous = Console.In;
		using var cancellation = new CancellationTokenSource();
		using var input = new CancelOnRead("preset set prod ApiKey=synthetic-secret", cancellation);
		try {
			Logger.SetInfoPathForTest(path);
			Console.SetIn(input);
			await CliLoop.RunAsync(() => Assert.Fail("Discarded input requested exit"), cancellation.Token);
			var log = File.ReadAllText(path);
			Assert.Contains(string.Format(L.Get("CliInputDiscarded"), "preset"), log);
			Assert.DoesNotContain("synthetic-secret", log);
			Assert.DoesNotContain("prod", log);
		} finally {
			Console.SetIn(previous);
			Logger.SetInfoPathForTest(null);
			File.Delete(path);
		}
	}

	[Fact]
	public async Task Help_ListsCommands() {
		var result = await CliDispatcher.ExecuteAsync("help");
		Assert.DoesNotContain("reload", result.Output);
		Assert.Contains("db backup", result.Output);
		Assert.Contains("sync", result.Output);
		Assert.False(result.ShouldExit);
	}

	[Theory]
	[InlineData("en-US")]
	[InlineData("zh-CN")]
	public async Task Status_ShowsGatewayAndCounters(string cultureName) {
		var culture = CultureInfo.GetCultureInfo(cultureName);
		var originalCulture = CultureInfo.CurrentCulture;
		var originalUICulture = CultureInfo.CurrentUICulture;
		CultureInfo.CurrentCulture = culture;
		CultureInfo.CurrentUICulture = culture;
		try {
			var result = await CliDispatcher.ExecuteAsync("status");
			var statusLines = L.Get("CliStatus").Split('\n');
			var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(CliDispatcher).Assembly.Location).ProductVersion!.Split('+')[0];
			Assert.StartsWith($"Pengin1011 {version}\n", result.Output.ReplaceLineEndings("\n"));
			Assert.Contains(statusLines[2].Split('{')[0].TrimEnd(), result.Output);
			Assert.Contains(statusLines[4].Split('{')[0].TrimEnd(), result.Output);
			Assert.Contains(statusLines[5].Split('{')[0].TrimEnd(), result.Output);
		} finally {
			CultureInfo.CurrentCulture = originalCulture;
			CultureInfo.CurrentUICulture = originalUICulture;
		}
	}

	[Fact]
	public async Task Status_UnconfiguredModel_ShowsPlaceholder() {
		var path = Path.Combine(Path.GetTempPath(), $"pengin1011_status_{Guid.NewGuid():N}.json");
		File.WriteAllText(path, """
			{
			  "Discord": { "Token": "tok" }
			}
			""");
		try {
			Assert.True(AppConfig.TryLoad(path, out _));

			var result = await CliDispatcher.ExecuteAsync("status");

			Assert.Contains(L.Get("AIModelNotConfigured"), result.Output);
		} finally {
			File.Delete(path);
		}
	}

	[Fact]
	public async Task Modules_WithNoneLoaded() {
		ModuleHost.ResetForTest();
		var result = await CliDispatcher.ExecuteAsync("modules");
		Assert.Contains(L.Get("NoModulesLoaded"), result.Output);
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
			Assert.Contains(L.Get("ReloadRemoved"), result.Output);
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
		var path = Path.Combine(Path.GetTempPath(), "pengin_cli_config_" + Guid.NewGuid().ToString("N") + ".json");
		try {
			File.WriteAllText(path, "{\"Discord\":{\"Token\":\"test\"}}");
			Assert.True(AppConfig.TryLoad(path, out _));
			var result = await CliDispatcher.ExecuteAsync("db");
			Assert.Contains(L.Get("DbInternalUsage"), result.Output);
		} finally {
			File.Delete(path);
		}
	}

	[Fact]
	public async Task UnknownCommand_Hinted() {
		var result = await CliDispatcher.ExecuteAsync("foobar");
		Assert.Contains(L.Prefix("UnknownCommand"), result.Output);
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
