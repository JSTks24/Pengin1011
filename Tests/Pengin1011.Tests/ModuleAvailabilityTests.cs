using System.Runtime.Loader;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Pengin1011.Core;
using Pengin1011.Core.Logging;
using Pengin1011.Core.Modules;
using Pengin1011.Services.Discord;
using Pengin1011.Modules.FakeModule;
using Pengin1011.Modules.FakeSecond;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ModuleAvailabilityTests {
	private async Task<InteractionService> BootAsync(Action<LoadedModule>? beforeInit = null) {
		ModuleHostTests.ResetModules();
		InteractionHost.ResetForTest();
		DiscordGateway.Init(new DiscordSocketClient());
		ModuleHost.LoadAll();
		if (beforeInit != null) {
			foreach (var run in ModuleHost.Modules) {
				beforeInit(run);
			}
		}
		await InteractionHost.PublishInitialAsync();
		foreach (var run in ModuleHost.Modules) {
			if (run.InitTask != null) {
				try {
					await run.InitTask.WaitAsync(TimeSpan.FromSeconds(2));
				} catch (TimeoutException) {
				} catch (Exception) {
				}
			}
		}
		await Task.Delay(100);
		return InteractionHost.Current.Service;
	}

	private Task TeardownAsync() {
		foreach (var run in ModuleHost.Modules) {
			run.Lifecycle.Cancel();
		}
		InteractionHost.ResetForTest();
		ModuleHostTests.ResetModules();
		return Task.CompletedTask;
	}

	private static LoadedModule FakeRun() {
		return ModuleHost.Modules.Single(module => module.Name == "FakeModule");
	}

	private static (IInteractionContext Context, object Interaction) MakeContext(LoadedModule run, string commandName) {
		var interactionType = run.Assembly.GetType("Pengin1011.Modules.FakeModule.FakeInteraction")!;
		var interaction = Activator.CreateInstance(interactionType, commandName)!;
		var contextType = run.Assembly.GetType("Pengin1011.Modules.FakeModule.FakeInteractionContext")!;
		return ((IInteractionContext)Activator.CreateInstance(contextType, interaction)!, interaction);
	}

	private static IReadOnlyList<string> Responses(object interaction) {
		return (IReadOnlyList<string>)interaction.GetType().GetProperty("Responses")!.GetValue(interaction)!;
	}

	[Fact]
	public async Task Execute_WhenReady_CommandBodyRuns() {
		var service = await BootAsync();
		try {
			var run = FakeRun();
			var (context, interaction) = MakeContext(run, "fakeping");
			await service.ExecuteCommandAsync(context, EmptyServiceProvider.Instance);

			Assert.Equal(1, ModuleProbe.CommandStaticInt(run, "FakeModule", "CommandCount"));
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task Execute_WhenDisabled_ZeroCommandBodyExecution() {
		Func<Exception> failure = () => new InvalidOperationException("配置损坏");
		var service = await BootAsync(run => {
			if (run.Name == "FakeModule") {
				ModuleProbe.SetRuntimeStatic(run, "OnInitFailure", failure);
			}
		});
		try {
			var run = FakeRun();
			Assert.Equal(ModuleState.Disabled, run.State);

			var (context, interaction) = MakeContext(run, "fakeping");
			await service.ExecuteCommandAsync(context, EmptyServiceProvider.Instance);

			Assert.Equal(0, ModuleProbe.CommandStaticInt(run, "FakeModule", "CommandCount"));
			Assert.Contains(Responses(interaction), static response => response.Contains(L.Get("ModuleUnavailable")));
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task Execute_WhenStarting_ZeroCommandBodyExecution() {
		var initGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var service = await BootAsync(run => {
			if (run.Name == "FakeModule") {
				ModuleProbe.SetRuntimeStatic(run, "InitGate", initGate);
			}
		});
		try {
			var run = FakeRun();
			Assert.Equal(ModuleState.Starting, run.State);

			var (context, interaction) = MakeContext(run, "fakeping");
			await service.ExecuteCommandAsync(context, EmptyServiceProvider.Instance);

			Assert.Equal(0, ModuleProbe.CommandStaticInt(run, "FakeModule", "CommandCount"));
			Assert.Contains(Responses(interaction), static response => response.Contains(L.Get("ModuleUnavailable")));
		} finally {
			initGate.SetResult();
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task Execute_AlreadyResponded_DoesNotRespondAgain() {
		Func<Exception> failure = () => new InvalidOperationException("配置损坏");
		var service = await BootAsync(run => {
			if (run.Name == "FakeModule") {
				ModuleProbe.SetRuntimeStatic(run, "OnInitFailure", failure);
			}
		});
		try {
			var run = FakeRun();
			var (context, interaction) = MakeContext(run, "fakeping");
			interaction.GetType().GetProperty("HasResponded")!.SetValue(interaction, true);
			await service.ExecuteCommandAsync(context, EmptyServiceProvider.Instance);

			Assert.Equal(0, ModuleProbe.CommandStaticInt(run, "FakeModule", "CommandCount"));
			Assert.Empty(Responses(interaction));
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task Execute_CommandThrows_ExceptionLoggedWithFullChain() {
		var logPath = Path.Combine(Path.GetTempPath(), $"qq1011_log_{Guid.NewGuid():N}.log");
		var service = await BootAsync();
		try {
			Pengin1011.Core.Logging.Logger.SetLogPathForTest(logPath);
			var run = FakeRun();
			ModuleProbe.SetCommandStatic(run, "FakeModule", "CommandFailure", new InvalidOperationException("命令体爆炸"));
			var (context, _) = MakeContext(run, "fakeping");

			await service.ExecuteCommandAsync(context, EmptyServiceProvider.Instance);

			Pengin1011.Core.Logging.Logger.SetLogPathForTest(null);
			var content = File.ReadAllText(logPath);
			Assert.Contains(L.Prefix("CommandExecutionException"), content);
			Assert.Contains("命令体爆炸", content);
		} finally {
			Pengin1011.Core.Logging.Logger.SetLogPathForTest(null);
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task Execute_RepeatedCommands_ShareSingleInitialization() {
		var service = await BootAsync();
		try {
			var run = FakeRun();
			for (var i = 0; i < 3; i++) {
				var (context, _) = MakeContext(run, "fakeping");
				await service.ExecuteCommandAsync(context, EmptyServiceProvider.Instance);
			}
			Assert.Equal(3, ModuleProbe.CommandStaticInt(run, "FakeModule", "CommandCount"));
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
		} finally {
			await TeardownAsync();
		}
	}

	private static void CopyBareIntoModuleDir() {
		var barePath = Path.Combine(AppContext.BaseDirectory, "baremodule", "FakeBare.dll");
		Assert.True(File.Exists(barePath), "FakeBare.dll 缺失：测试资产应由构建复制保障");
		File.Copy(barePath, Path.Combine(AppContext.BaseDirectory, "module", "FakeBare.dll"), true);
	}

	private static void MoveOutAllFakeDlls() {
		var moduleDir = Path.Combine(AppContext.BaseDirectory, "module");
		foreach (var name in new[] { "FakeModule", "FakeSecond", "FakeThird" }) {
			var path = Path.Combine(moduleDir, name + ".dll");
			if (File.Exists(path)) File.Move(path, path + ".bak");
		}
	}

	private static void RestoreAllFakeDlls() {
		var moduleDir = Path.Combine(AppContext.BaseDirectory, "module");
		foreach (var name in new[] { "FakeModule", "FakeSecond", "FakeThird" }) {
			var bakPath = Path.Combine(moduleDir, name + ".dll.bak");
			if (File.Exists(bakPath)) File.Move(bakPath, Path.Combine(moduleDir, name + ".dll"));
		}
	}

	[Fact]
	public async Task C01_Startup_MixedBareModule_LegalServes_BareRejected() {
		CopyBareIntoModuleDir();
		var logPath = Path.Combine(Path.GetTempPath(), $"qq1011_c01_{Guid.NewGuid():N}.log");
		Logger.SetLogPathForTest(logPath);
		try {
			var service = await BootAsync();
			Logger.SetLogPathForTest(null);

			Assert.Contains(service.SlashCommands, command => command.Name == "fakeping");
			Assert.DoesNotContain(service.SlashCommands, command => command.Name == "fakebare");

			var bareRun = ModuleHost.Modules.Single(module => module.Name == "FakeBare");
			Assert.Equal(ModuleState.Disabled, bareRun.State);
			Assert.Null(bareRun.InitTask);
			Assert.NotNull(bareRun.DisabledReason);

			var run = FakeRun();
			var (context, interaction) = MakeContext(run, "fakeping");
			await service.ExecuteCommandAsync(context, EmptyServiceProvider.Instance);
			Assert.Equal(1, ModuleProbe.CommandStaticInt(run, "FakeModule", "CommandCount"));

			var content = File.ReadAllText(logPath);
			Assert.Contains(L.Prefix("StartupAttachContractError"), content);
			Assert.Contains("FakeBare", content);
		} finally {
			Logger.SetLogPathForTest(null);
			File.Delete(Path.Combine(AppContext.BaseDirectory, "module", "FakeBare.dll"));
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task C02_RunModeAsyncOverride_RejectedAtAttach_ZeroCommandBodyExecution() {
		ModuleHostTests.ResetModules();
		var client = new DiscordSocketClient();
		var service = new InteractionService(client.Rest, new InteractionServiceConfig {
			AutoServiceScopes = false,
			DefaultRunMode = RunMode.Sync,
			LogLevel = LogSeverity.Warning,
		});
		try {
			var assembly = typeof(C02AsyncOverrideModule).Assembly;
			var run = new LoadedModule {
				Name = "C02AsyncOverride",
				FilePath = "",
				Assembly = assembly,
				LoadedAt = DateTimeOffset.Now,
				Runtime = new FakeSecondRuntime(),
			};
			var attach = await InteractionHost.AttachAsync(service, [run]);

			Assert.Equal(run, Assert.Single(attach.Invalid));
			Assert.Contains(attach.Errors, error => error.Contains("RunMode"));
			Assert.Equal(0, C02AsyncOverrideModule.CommandCount);
		} finally {
			ModuleHostTests.ResetModules();
		}
	}

	[Fact]
	public async Task C03_RuntimeTypeMismatch_Rejected() {
		ModuleHostTests.ResetModules();
		var client = new DiscordSocketClient();
		var service = new InteractionService(client.Rest, new InteractionServiceConfig {
			AutoServiceScopes = false,
			DefaultRunMode = RunMode.Sync,
			LogLevel = LogSeverity.Warning,
		});
		try {
			var foreign = new ModuleAvailabilityAttribute(typeof(FakeSecondRuntime));
			var direct = await foreign.CheckRequirementsAsync(null!, null!, null!);
			Assert.False(direct.IsSuccess);

			var moduleDll = Path.Combine(AppContext.BaseDirectory, "module", "FakeModule.dll");
			Assert.True(File.Exists(moduleDll), "FakeModule.dll 缺失：测试资产应由构建复制保障");
			var copy = ModuleHost.LoadOne("FakeModule", moduleDll);
			var mismatch = new LoadedModule {
				Name = "FakeModule",
				FilePath = copy.FilePath,
				Assembly = copy.Assembly,
				LoadedAt = DateTimeOffset.Now,
				Runtime = new FakeSecondRuntime(),
			};
			var attach = await InteractionHost.AttachAsync(service, [mismatch]);
			Assert.Equal(mismatch, Assert.Single(attach.Invalid));
			Assert.Contains(attach.Errors, error => error.Contains(mismatch.Name) && error.Contains(foreign.RuntimeType.Name));
		} finally {
			ModuleHostTests.ResetModules();
		}
	}

	[Fact]
	public async Task C04_RebuildAfterInvalidCandidate_LegalModulesInitExactlyOnce() {
		CopyBareIntoModuleDir();
		try {
			await BootAsync();

			var bareRun = ModuleHost.Modules.Single(module => module.Name == "FakeBare");
			Assert.Null(bareRun.InitTask);
			Assert.Equal(ModuleState.Disabled, bareRun.State);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(FakeRun(), "InitCount"));
			Assert.DoesNotContain(bareRun, InteractionHost.Current.Modules);
			Assert.Contains(FakeRun(), InteractionHost.Current.Modules);
		} finally {
			File.Delete(Path.Combine(AppContext.BaseDirectory, "module", "FakeBare.dll"));
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task C05_AllModulesIllegal_ZeroBusinessHost_WithDiagnostics() {
		MoveOutAllFakeDlls();
		CopyBareIntoModuleDir();
		var logPath = Path.Combine(Path.GetTempPath(), $"qq1011_c05_{Guid.NewGuid():N}.log");
		Logger.SetLogPathForTest(logPath);
		try {
			var service = await BootAsync();
			Logger.SetLogPathForTest(null);

			var bareRun = Assert.Single(ModuleHost.Modules);
			Assert.Equal("FakeBare", bareRun.Name);
			Assert.Equal(ModuleState.Disabled, bareRun.State);
			Assert.Empty(InteractionHost.Current.Modules);
			Assert.Empty(service.SlashCommands);
			Assert.True(DiscordGateway.Dispatcher!.Accepting);

			var content = File.ReadAllText(logPath);
			Assert.Contains(L.Prefix("StartupAttachContractError"), content);
			Assert.Contains("FakeBare", content);
		} finally {
			Logger.SetLogPathForTest(null);
			File.Delete(Path.Combine(AppContext.BaseDirectory, "module", "FakeBare.dll"));
			RestoreAllFakeDlls();
			await TeardownAsync();
		}
	}
}

[ModuleAvailability(typeof(FakeSecondRuntime))]
public class C02AsyncOverrideModule : InteractionModuleBase<FakeInteractionContext> {
	public static int CommandCount;

	[SlashCommand("c02_async_override", "RunMode 覆盖拒绝用例", false, RunMode.Async)]
	public Task Body() {
		CommandCount++;
		return Task.CompletedTask;
	}
}