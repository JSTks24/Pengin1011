using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Pengin1011;
using Pengin1011.Core;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;
using Pengin1011.Services.AI;
using Pengin1011.Services.Discord;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var GatewayStartTimeout = TimeSpan.FromSeconds(45);
var commandLine = Environment.GetCommandLineArgs();
if (commandLine.Length >= 3 && (commandLine[1] == "startup-persist-write" || commandLine[1] == "startup-persist-verify")) {
	return await StartupPersistProbeAsync(commandLine[2], commandLine[1] == "startup-persist-verify");
}

var exitRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var stopCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => {
	eventArgs.Cancel = true;
	stopCts.Cancel();
	exitRequested.TrySetResult();
};

async Task<int> FinishAsync(bool started) {
	var report = await HostExit.StopAsync();
	return HostExit.CodeFor(started, report);
}

var started = false;
try {
	if (!AppConfig.TryLoad(null, out _)) {
		return await FinishAsync(started);
	}
	var model = AppConfig.AI.Provider == AIProvider.OpenAI ? AppConfig.AI.OpenAI.Model : AppConfig.AI.Gemini.Model;
	Logger.Info(typeof(Program), Localizer.Format("ConfigLoaded", AppConfig.AI.Provider, model, AppConfig.AI.MaxParallel));

	Databases.Init();
	AIClient.Init(AppConfig.AI.OpenAI, AppConfig.AI.Gemini, AppConfig.AI.Provider, AppConfig.AI.MaxParallel);

	DiscordGateway.Init(AppConfig.Discord.Token);
	Components.Attach();
	DiscordGateway.SubscribeInteraction(null, InteractionHost.ExecuteAsync);

	ModuleHost.LoadAll();
	await InteractionHost.PublishInitialAsync();

	using (var gatewayStart = CancellationTokenSource.CreateLinkedTokenSource(stopCts.Token)) {
		gatewayStart.CancelAfter(GatewayStartTimeout);
		try {
			if (!await DiscordGateway.StartAsync() || !await DiscordGateway.WaitReadyAsync(ct: gatewayStart.Token)) {
				return await FinishAsync(started);
			}
		} catch (OperationCanceledException) when (gatewayStart.IsCancellationRequested) {
			Logger.Error(typeof(Program), Localizer.Format("GatewayStartupTimeout", GatewayStartTimeout.TotalSeconds));
			return await FinishAsync(started);
		}
	}
	started = true;
	Logger.Info(typeof(Program), Localizer.Get("StartedHint"));

	CliDispatcher.StartedAt = DateTimeOffset.Now;
	_ = CliLoop.RunAsync(() => exitRequested.TrySetResult(), stopCts.Token, () => HostExit.Requested);

	var completed = await Task.WhenAny(exitRequested.Task, Task.Delay(Timeout.Infinite, stopCts.Token));
	if (completed != exitRequested.Task && !completed.IsCanceled) {
		await completed;
	}
	return await FinishAsync(started);
} catch (Exception e) {
	Logger.Error(typeof(Program), e, Localizer.Get("StartupFailed"));
	return await FinishAsync(started);
} finally {
	await HostExit.StopAsync();
}

async Task<int> StartupPersistProbeAsync(string baseDir, bool verify) {
	using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(2));
	var probe = RunStartupPersistProbeAsync(baseDir, verify, watchdog.Token);
	var finished = await Task.WhenAny(probe, Task.Delay(Timeout.Infinite, watchdog.Token));
	if (finished != probe) {
		Logger.Error(typeof(Program), "Child process probe did not finish within 2 minutes; forcing process exit");
		Environment.Exit(6);
	}
	return await probe;
}

async Task<int> RunStartupPersistProbeAsync(string baseDir, bool verify, CancellationToken ct) {
	Databases.SetDataDirectoryForTest(baseDir);
	if (!AppConfig.TryLoad(Path.Combine(baseDir, "config.json"), out var errors)) {
		Logger.Error(typeof(Program), $"Child process config load failed: {string.Join("; ", errors)}");
		return 2;
	}
	Databases.Init();
	DiscordGateway.Init(new DiscordSocketClient(new DiscordSocketConfig {
		ConnectionTimeout = (int)GatewayStartTimeout.TotalMilliseconds,
	}));
	ModuleHost.LoadAll();
	await InteractionHost.PublishInitialAsync();
	foreach (var run in ModuleHost.Modules) {
		if (run.InitTask == null) continue;
		try {
			await run.InitTask.WaitAsync(TimeSpan.FromSeconds(5));
			} catch (Exception e) {
				Logger.Error(typeof(Program), e, $"Child process module initialization did not complete: {run.Name}");
			}
	}
	var moduleRun = ModuleHost.Modules.FirstOrDefault(module => module.Name == "FakeModule");
	if (moduleRun == null) {
		Logger.Error(typeof(Program), "Child process did not load FakeModule; cannot verify the database contract");
		return 3;
	}
	var contextType = moduleRun.Assembly.GetType("Pengin1011.Modules.FakeModule.FakeModuleDbContext");
	if (contextType == null) {
		Logger.Error(typeof(Program), "Child process could not find FakeModuleDbContext");
		return 4;
	}
	var open = typeof(Databases).GetMethod(nameof(Databases.Open))!.MakeGenericMethod(contextType);
	var context = (DbContext)open.Invoke(null, null)!;
	await using (context) {
		ct.ThrowIfCancellationRequested();
		if (verify) {
			var names = await context.Database.SqlQuery<string>($"SELECT \"Name\" AS \"Value\" FROM \"Items\"").ToListAsync();
			if (names.Count != 1 || names[0] != "ProcessAWrite") {
				Logger.Error(typeof(Program), $"Child process could not read process A's write: [{string.Join(", ", names)}]");
				return 5;
			}
		} else {
			var itemType = moduleRun.Assembly.GetType("Pengin1011.Modules.FakeModule.FakeItem");
			if (itemType == null) {
				Logger.Error(typeof(Program), "Child process could not find the FakeItem entity type");
				return 7;
			}
			var item = Activator.CreateInstance(itemType)!;
			itemType.GetProperty("Name")!.SetValue(item, "ProcessAWrite");
			context.Add(item);
			await context.SaveChangesAsync(ct);
		}
	}
	var report = await HostExit.StopAsync();
	if (!report.Success) {
		Logger.Error(typeof(Program), $"Child process exit report failed: {string.Join("; ", report.Failures)}");
		return 6;
	}
	return 0;
}
