using System.Diagnostics;
using Discord.WebSocket;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pengin1011.Core;
using Pengin1011.Core.Logging;
using Pengin1011.Core.Modules;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

internal static class TestChildProcess {
	private static readonly TimeSpan _budget = TimeSpan.FromMinutes(3);

	public static async Task RunAsync(string mode) {
		await RunArgumentsAsync(mode.Split(' ', StringSplitOptions.RemoveEmptyEntries));
	}

	internal static ProcessStartInfo StartInfo(params string[] arguments) {
		var appDll = Path.Combine(AppContext.BaseDirectory, "Pengin1011.Tests.dll");
		if (!File.Exists(appDll)) {
			throw new InvalidOperationException($"宿主程序集缺失：{appDll}（测试资产应由构建复制保障）");
		}
		var startInfo = new ProcessStartInfo("dotnet") {
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		startInfo.ArgumentList.Add("exec");
		startInfo.ArgumentList.Add(appDll);
		foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
		return startInfo;
	}

	internal static async Task RunArgumentsAsync(params string[] arguments) {
		var startInfo = StartInfo(arguments);
		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("子进程启动失败");
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		using var budget = new CancellationTokenSource(_budget);
		try {
			await process.WaitForExitAsync(budget.Token);
		} catch (OperationCanceledException) {
			process.Kill(true);
			throw new TimeoutException($"子进程 {string.Join(" ", arguments)} 未在 {_budget.TotalMinutes} 分钟内结束，已强制结束");
		}
		var output = await stdout;
		var error = await stderr;
		if (process.ExitCode != 0) {
			throw new InvalidOperationException($"子进程 {string.Join(" ", arguments)} 退出码 {process.ExitCode}：{output}{error}");
		}
	}

	public static void RemoveStaleArtifacts(params string[] directories) {
		foreach (var directory in directories) {
			if (Directory.Exists(directory)) {
				Directory.Delete(directory, true);
			}
		}
	}
	private static readonly TimeSpan _gatewayStartTimeout = TimeSpan.FromSeconds(45);

	public static async Task<int> Main(string[] args) {
		if (args.Length == 0) return 2;
		return args[0] switch {
			"maintenance-admission-probe" when args.Length == 2 => await HostMaintenanceAdmissionTests.RunProbeAsync(args[1]),
			"restart-probe" when args.Length == 3 => await RestartHandoffTests.RunProbeAsync(args[1], args[2]),
			"signal-probe" when args.Length == 2 => await ShutdownSignalsTests.RunProbeAsync(args[1]),
			"module-name-reject" when args.Length == 3 => RejectModuleName(args[1], args[2]),
			"startup-persist-write" or "startup-persist-verify" when args.Length == 2 => await StartupPersistProbeAsync(args[1], args[0] == "startup-persist-verify"),
			"db-seed" when args.Length == 2 => await SeedDatabaseAsync(args[1]),
			"db-interrupt" when args.Length == 5 => await InterruptDatabaseAsync(args[1], args[2], args[3], args[4]),
			"db-expect-blocked" when args.Length == 2 => await ExpectBlockedDatabaseAsync(args[1]),
			"db-recover" when args.Length == 4 => RecoverDatabase(args[1], args[2], args[3]),
			"db-read" when args.Length == 3 => await ReadDatabaseAsync(args[1], args[2]),
			"module-contract-reject" when args.Length == 2 => RejectLegacyContract(args[1]),
			_ => 2
		};
	}

	private static int RejectModuleName(string path, string assemblyName) {
		var name = Path.GetFileNameWithoutExtension(path);
		try {
			ModuleHost.LoadOne(name, path);
			Logger.Error(typeof(TestChildProcess), "Renamed module unexpectedly passed the assembly name check");
			return 3;
		} catch (InvalidOperationException e) {
			var expected = Loc.Format("ModuleAssemblyNameMismatch", name, assemblyName);
			if (e.Message != expected) {
				Logger.Error(typeof(TestChildProcess), e, "Renamed module was rejected for an unexpected reason");
				return 4;
			}
			Logger.Info(typeof(TestChildProcess), e.Message);
		}
		foreach (var module in new[] { name, assemblyName }) {
			try {
				var call = ModuleBridgeDispatcher.Admit<Func<string>>(module, "RenamedValue");
				call.Lease.Dispose();
				Logger.Error(typeof(TestChildProcess), "Rejected renamed module left a bridge export registered");
				return 5;
			} catch (KeyNotFoundException e) {
				Logger.Info(typeof(TestChildProcess), $"Rejected renamed module left no bridge export: {e.Message}");
			}
		}
		var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == assemblyName);
		return ModuleHost.Find(name) == null && ModuleRegistry.RunOf(assembly) == null ? 0 : 6;
	}

	private static int RejectLegacyContract(string path) {
		try {
			ModuleHost.LoadOne("LegacyContract", path);
			Logger.Error(typeof(TestChildProcess), "Legacy module unexpectedly passed the host contract check");
			return 3;
		} catch (InvalidOperationException e) {
			var expected = Loc.Format("HostContractMismatch", "LegacyContract", 3, ModuleHostContract.Current);
			if (e.Message != expected) {
				Logger.Error(typeof(TestChildProcess), e, "Legacy module was rejected for an unexpected reason");
				return 4;
			}
			Logger.Info(typeof(TestChildProcess), e.Message);
		}
		try {
			var call = ModuleBridgeDispatcher.Admit<Func<string>>("LegacyContract", "LegacyValue");
			call.Lease.Dispose();
			Logger.Error(typeof(TestChildProcess), "Rejected module left a bridge export registered");
			return 5;
		} catch (KeyNotFoundException e) {
			Logger.Info(typeof(TestChildProcess), $"Rejected module left no bridge export: {e.Message}");
		}
		return ModuleHost.Find("LegacyContract") == null ? 0 : 6;
	}

	private static void InitializeDatabase(string directory) {
		Databases.ConfigureDataDirectory(directory);
		Databases.Init();
	}

	private static async Task<int> SeedDatabaseAsync(string directory) {
		InitializeDatabase(directory);
		await Databases.PrepareAsync<FakeDbContext>();
		await using (var database = Databases.Open<FakeDbContext>()) {
			database.Items.Add(new FakeItem { Name = "backup-target" });
			await database.SaveChangesAsync();
		}
		var backup = await Databases.BackupNowAsync();
		if (!backup.Success) return 3;
		File.WriteAllText(Path.Combine(directory, "backup-id.txt"), Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory).Single().BackupId);
		await using (var database = Databases.Open<FakeDbContext>()) {
			var item = await database.Items.SingleAsync();
			item.Name = "live-target";
			await database.SaveChangesAsync();
		}
		await Databases.ShutdownAsync();
		Databases.ReleaseOwnership();
		SqliteConnection.ClearAllPools();
		return 0;
	}

	private static async Task<int> InterruptDatabaseAsync(string directory, string backupId, string stage, string signal) {
		InitializeDatabase(directory);
		DatabaseRestoreEngine.BoundaryForTest = boundary => {
			if (boundary != stage) return;
			File.WriteAllText(signal, boundary);
			using var blocked = new ManualResetEventSlim();
			blocked.Wait();
		};
		var restored = await Databases.RestoreAsync("Fake", backupId);
		Logger.Error(typeof(TestChildProcess), $"Restore unexpectedly passed interruption boundary {stage}: {restored.Outcome}");
		return 3;
	}

	private static async Task<int> ExpectBlockedDatabaseAsync(string directory) {
		InitializeDatabase(directory);
		try {
			await Databases.PrepareAsync<FakeDbContext>();
			Logger.Error(typeof(TestChildProcess), "Interrupted restore unexpectedly allowed database preparation");
			return 3;
		} catch (InvalidOperationException e) {
			Logger.Info(typeof(TestChildProcess), $"Interrupted restore blocked database preparation: {e.Message}");
			return Databases.StateOf<FakeDbContext>() == DatabaseState.MaintenanceRequired ? 0 : 4;
		}
	}

	private static int RecoverDatabase(string directory, string operationId, string mode) {
		InitializeDatabase(directory);
		var result = mode == "--complete" ? Databases.CompleteRestore("Fake", operationId) : Databases.RollbackRestore("Fake", operationId);
		if (!result.Success) {
			Logger.Error(typeof(TestChildProcess), $"Recovery failed: {result.Reason}");
			return 3;
		}
		Databases.ReleaseOwnership();
		return 0;
	}

	private static async Task<int> ReadDatabaseAsync(string directory, string expected) {
		InitializeDatabase(directory);
		await Databases.PrepareAsync<FakeDbContext>();
		await using (var database = Databases.Open<FakeDbContext>()) {
			var names = await database.Items.Select(item => item.Name).ToListAsync();
			if (names.Count != 1 || names[0] != expected) {
				Logger.Error(typeof(TestChildProcess), $"Database target mismatch: expected {expected}, found {string.Join(",", names)}");
				return 3;
			}
		}
		await Databases.ShutdownAsync();
		Databases.ReleaseOwnership();
		return 0;
	}

	private static async Task<int> StartupPersistProbeAsync(string baseDir, bool verify) {
		using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(2));
		var probe = RunStartupPersistProbeAsync(baseDir, verify, watchdog.Token);
		var finished = await Task.WhenAny(probe, Task.Delay(Timeout.Infinite, watchdog.Token));
		if (finished != probe) {
			Logger.Error(typeof(TestChildProcess), "Child process probe did not finish within 2 minutes; forcing process exit");
			Environment.Exit(6);
		}
		return await probe;
	}

	private static async Task<int> RunStartupPersistProbeAsync(string baseDir, bool verify, CancellationToken ct) {
		Databases.SetDataDirectoryForTest(Path.Combine(baseDir, "data"));
		if (!AppConfig.TryLoad(Path.Combine(baseDir, "config.json"), out var errors)) {
			Logger.Error(typeof(TestChildProcess), $"Child process config load failed: {string.Join("; ", errors)}");
			return 2;
		}
		Databases.Init();
		DiscordGateway.Init(new DiscordSocketClient(new DiscordSocketConfig {
			ConnectionTimeout = (int)_gatewayStartTimeout.TotalMilliseconds,
		}));
		ModuleHost.LoadAll();
		await InteractionHost.PublishInitialAsync();
		foreach (var run in ModuleHost.Modules) {
			if (run.InitTask == null) continue;
			try {
				await run.InitTask.WaitAsync(TimeSpan.FromSeconds(5));
			} catch (Exception e) {
				Logger.Error(typeof(TestChildProcess), e, $"Child process module initialization did not complete: {run.Name}");
			}
		}
		var moduleRun = ModuleHost.Modules.FirstOrDefault(module => module.Name == "FakeModule");
		if (moduleRun == null) {
			Logger.Error(typeof(TestChildProcess), "Child process did not load FakeModule; cannot verify the database contract");
			return 3;
		}
		var contextType = moduleRun.Assembly.GetType("Pengin1011.Modules.FakeModule.FakeModuleDbContext");
		if (contextType == null) {
			Logger.Error(typeof(TestChildProcess), "Child process could not find FakeModuleDbContext");
			return 4;
		}
		var open = typeof(Databases).GetMethod(nameof(Databases.Open))!.MakeGenericMethod(contextType);
		var context = (DbContext)open.Invoke(null, null)!;
		await using (context) {
			ct.ThrowIfCancellationRequested();
			if (verify) {
				var names = await context.Database.SqlQuery<string>($"SELECT \"Name\" AS \"Value\" FROM \"Items\"").ToListAsync();
				if (names.Count != 1 || names[0] != "ProcessAWrite") {
					Logger.Error(typeof(TestChildProcess), $"Child process could not read process A's write: [{string.Join(", ", names)}]");
					return 5;
				}
			} else {
				var itemType = moduleRun.Assembly.GetType("Pengin1011.Modules.FakeModule.FakeItem");
				if (itemType == null) {
					Logger.Error(typeof(TestChildProcess), "Child process could not find the FakeItem entity type");
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
			Logger.Error(typeof(TestChildProcess), $"Child process exit report failed: {string.Join("; ", report.Failures)}");
			return 6;
		}
		return 0;
	}
}
