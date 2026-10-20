using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Pengin1011.Core;
using Pengin1011.Core.Localization;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class HostMaintenanceTests {
	private static string TempDirectory() {
		var directory = Path.Combine(Path.GetTempPath(), "pengin_host_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		return directory;
	}

	private static async Task<string> SeedAsync(string directory) {
		await TestChildProcess.RunArgumentsAsync("db-seed", directory);
		return File.ReadAllText(Path.Combine(directory, "backup-id.txt"));
	}

	private static async Task FillRollingBackupsAsync(string directory, int count) {
		Databases.SetDataDirectoryForTest(directory);
		Databases.Init();
		while (Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory).Count < count) {
			Assert.True((await Databases.BackupNowAsync()).Success);
		}
	}

	private static async Task<string> InterruptAsync(string directory, string backupId, string stage) {
		var signal = Path.Combine(directory, "interrupted.txt");
		using var process = Process.Start(TestChildProcess.StartInfo("db-interrupt", directory, backupId, stage, signal))!;
		var output = process.StandardOutput.ReadToEndAsync();
		var error = process.StandardError.ReadToEndAsync();
		using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		try {
			while (!File.Exists(signal)) {
				if (process.HasExited) {
					throw new InvalidOperationException($"Restore child exited before boundary {stage}: {await output}{await error}");
				}
				await Task.Delay(20, budget.Token);
			}
			process.Kill(true);
			await process.WaitForExitAsync(budget.Token);
			var record = MaintenanceJournal.ListUnfinishedRestores(directory, "Fake").Single();
			Assert.Null(record.Failure);
			return record.OperationId;
		} finally {
			if (!process.HasExited) {
				process.Kill(true);
				await process.WaitForExitAsync();
			}
			await output;
			await error;
		}
	}

	private static string ReadName(string directory) {
		using var connection = SqliteConnectionPolicy.OpenReadOnly(Path.Combine(directory, "Fake.db"));
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT Name FROM Items";
		return (string)command.ExecuteScalar()!;
	}

	private static void Cleanup(string directory) {
		DatabaseRestoreEngine.BoundaryForTest = null;
		HostMaintenance.ResetForTest();
		Databases.SetDataDirectoryForTest(null);
		SqliteConnection.ClearAllPools();
		Directory.Delete(directory, true);
	}

	[Theory]
	[InlineData("QuarantineCopied", "--rollback", "live-target")]
	[InlineData("CutoverStarted", "--rollback", "live-target")]
	[InlineData("SidecarsMoved", "--rollback", "live-target")]
	[InlineData("MainReplaced", "--rollback", "live-target")]
	[InlineData("CutoverStarted", "--complete", "backup-target")]
	[InlineData("MainReplaced", "--complete", "backup-target")]
	public async Task KilledRestore_IsBlockedAndRecoveredAcrossIndependentProcesses(string stage, string recovery, string expected) {
		var directory = TempDirectory();
		try {
			var backupId = await SeedAsync(directory);
			var operation = await InterruptAsync(directory, backupId, stage);

			await TestChildProcess.RunArgumentsAsync("db-expect-blocked", directory);
			await TestChildProcess.RunArgumentsAsync("db-recover", directory, operation, recovery);
			await TestChildProcess.RunArgumentsAsync("db-read", directory, expected);

			Assert.Empty(MaintenanceJournal.ListUnfinishedRestores(directory, "Fake"));
			var completed = MaintenanceJournal.ReadRestoreRecord(directory, "Fake", operation)!;
			Assert.Equal(recovery == "--complete" ? "Completed" : stage == "QuarantineCopied" ? "Cancelled" : "RolledBack", completed.State);
		} finally {
			Cleanup(directory);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ActualProgramRecovery_RejectsMaintenanceOnlyReplacementAndKeepsOriginalTerminal(bool interrupted) {
		var root = TempDirectory();
		var directory = Path.Combine(root, "data");
		var runtime = CloneHost(root, "host");
		Process? original = null;
		Task<string>? error = null;
		try {
			var backupId = await SeedAsync(directory);
			if (!interrupted) {
				await FillRollingBackupsAsync(directory, DatabaseBackupStore.RollingKeepCount - 1);
				Databases.SetDataDirectoryForTest(null);
			}
			var value = interrupted ? await InterruptAsync(directory, backupId, "MainReplaced") : backupId;
			original = Process.Start(HostStartInfo(runtime, directory))!;
			error = original.StandardError.ReadToEndAsync();
			await WaitForMaintenanceConsoleAsync(original);
			if (!interrupted) {
				var backups = DatabaseBackupStore.ListCompleted(directory, "Fake", DatabaseBackupStore.RollingCategory);
				Assert.Equal(DatabaseBackupStore.RollingKeepCount, backups.Count);
				Assert.Equal(backupId, backups[0].BackupId);
			}
			await original.StandardInput.WriteLineAsync(interrupted
				? $"db recover Fake {value} --complete --confirm"
				: $"db restore Fake {value} --confirm");
			await WaitForMaintenanceConsoleAsync(original);

			Assert.False(original.HasExited);
			Assert.False(DataDirectoryOwnership.TryAcquire(directory, out _, out _));
			Assert.Equal("backup-target", ReadName(directory));
			Assert.Empty(MaintenanceJournal.ListUnfinishedRestores(directory, "Fake"));
			using (var replacement = FindReplacement(runtime, original.Id)) {
				Assert.True(replacement == null || replacement.HasExited);
			}

			await original.StandardInput.WriteLineAsync("db status Fake");
			await original.StandardInput.WriteLineAsync("exit");
			original.StandardInput.Close();
			var output = original.StandardOutput.ReadToEndAsync();
			await original.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
			Assert.Equal(1, original.ExitCode);
			Assert.Contains("Fake", await output);
			Assert.Contains(Localizer.Get(typeof(Databases).Assembly, "MaintenanceCompletedRestartFailed", CultureInfo.InvariantCulture), await error);
			Assert.True(DataDirectoryOwnership.TryAcquire(directory, out var ownership, out var failure), failure);
			ownership!.Dispose();
		} finally {
			if (original != null) {
				using var replacement = FindReplacement(runtime, original.Id);
				if (replacement != null && !replacement.HasExited) {
					replacement.Kill();
					await replacement.WaitForExitAsync();
				}
				if (!original.HasExited) {
					original.Kill();
					await original.WaitForExitAsync();
				}
				if (error != null) await error;
				original.Dispose();
			}
			Cleanup(root);
		}
	}

	[Fact]
	public async Task RestoreOldestSnapshot_RealHostExitKeepsTheChosenTargetThroughFinalBackup() {
		var directory = TempDirectory();
		HostExit.ResetForTest();
		ExitCoordinator.ResetForTest();
		InteractionHost.ResetForTest();
		ModuleHost.ResetForTest();
		DiscordGateway.DetachForTest();
		try {
			var backupId = await SeedAsync(directory);
			await FillRollingBackupsAsync(directory, DatabaseBackupStore.RollingKeepCount);
			var before = Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory);
			Assert.Equal(backupId, before[0].BackupId);
			Assert.Equal("live-target", ReadName(directory));
			Assert.Equal(L.Get("MaintenanceAccepted"), await DatabaseCommands.ExecuteAsync(["db", "restore", "Fake", backupId, "--confirm"]));
			var restartCount = 0;

			Assert.True(await HostMaintenance.ExecutePendingAsync(HostExit.StopAsync, () => {
				restartCount++;
				var after = Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory);
				Assert.Equal(DatabaseBackupStore.RollingKeepCount + 1, after.Count);
				Assert.Contains(after, backup => !before.Any(original => original.BackupId == backup.BackupId));
				Assert.Contains(after, backup => backup.BackupId == backupId);
				return true;
			}));

			Assert.Equal(1, restartCount);
			Assert.True((await HostExit.StopAsync()).ResourcesSettled);
			Assert.Equal("backup-target", ReadName(directory));
			Assert.False(HostMaintenance.Pending.IsCompleted);
			Assert.Null(HostMaintenance.ProtectedRestoreBackupId("Fake"));
			Assert.Empty(MaintenanceJournal.ListUnfinishedRestores(directory, "Fake"));
		} finally {
			HostExit.ResetForTest();
			ExitCoordinator.ResetForTest();
			InteractionHost.ResetForTest();
			ModuleHost.ResetForTest();
			Cleanup(directory);
		}
	}

	[Fact]
	public async Task AcceptedRestore_ProtectsTheOldestSnapshotUntilTheRequestFinishes() {
		var directory = TempDirectory();
		try {
			var backupId = await SeedAsync(directory);
			await FillRollingBackupsAsync(directory, DatabaseBackupStore.RollingKeepCount);
			Assert.Equal(L.Get("MaintenanceAccepted"), await DatabaseCommands.ExecuteAsync(["db", "restore", "Fake", backupId, "--confirm"]));
			Assert.Equal(backupId, HostMaintenance.ProtectedRestoreBackupId("fAkE"));
			Assert.Null(HostMaintenance.ProtectedRestoreBackupId("Other"));

			Assert.True((await Databases.BackupNowAsync()).Success);
			var protectedBackups = Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory);
			Assert.Equal(DatabaseBackupStore.RollingKeepCount + 1, protectedBackups.Count);
			Assert.Contains(protectedBackups, backup => backup.BackupId == backupId);
			Assert.False(await HostMaintenance.ExecutePendingAsync(
				() => Task.FromResult(new ExitCoordinator.ExitReport(false, ["still running"], false)),
				() => throw new InvalidOperationException("must not restart")));

			Assert.True((await Databases.BackupNowAsync()).Success);
			var unprotectedBackups = Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory);
			Assert.Equal(DatabaseBackupStore.RollingKeepCount, unprotectedBackups.Count);
			Assert.DoesNotContain(unprotectedBackups, backup => backup.BackupId == backupId);
		} finally {
			Cleanup(directory);
		}
	}

	[Fact]
	public async Task RestoreRequest_WaitsForEarlierPruningAndRejectsTheRemovedTarget() {
		var directory = TempDirectory();
		Task<string>? request = null;
		try {
			var backupId = await SeedAsync(directory);
			await FillRollingBackupsAsync(directory, DatabaseBackupStore.RollingKeepCount);
			await Databases.BackupGate.WaitAsync();
			try {
				request = DatabaseCommands.ExecuteAsync(["db", "restore", "Fake", backupId, "--confirm"]);
				Assert.False(request.IsCompleted);
				Assert.False(HostMaintenance.Pending.IsCompleted);
				Assert.True(DatabaseBackupStore.TryCreate(directory, "Fake", DatabaseBackupStore.RollingCategory,
					Path.Combine(directory, "Fake.db"), [], out _, out var failure), failure);
				Assert.True(DatabaseBackupStore.TryPruneRolling(directory, "Fake", out failure), failure);
			} finally {
				Databases.BackupGate.Release();
			}

			Assert.NotEqual(L.Get("MaintenanceAccepted"), await request.WaitAsync(TimeSpan.FromSeconds(5)));
			Assert.False(HostMaintenance.Busy);
			Assert.False(HostMaintenance.Pending.IsCompleted);
			Assert.DoesNotContain(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory), backup => backup.BackupId == backupId);
			Assert.Equal("live-target", ReadName(directory));
		} finally {
			if (request != null) await request;
			Cleanup(directory);
		}
	}

	private static Process? FindReplacement(string runtime, int originalId) {
		var log = Path.Combine(runtime, "runtime", "logs", "info.log");
		if (!File.Exists(log)) return null;
		foreach (Match match in Regex.Matches(File.ReadAllText(log), @"\(PID (\d+)\)").Reverse()) {
			var id = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
			if (id == originalId) continue;
			try {
				return Process.GetProcessById(id);
			} catch (ArgumentException) {
				return null;
			}
		}
		return null;
	}

	[Fact]
	public async Task RequestAcknowledgesBeforeDrain_AndFailedDrainBlocksFilesAndRestart() {
		var directory = TempDirectory();
		try {
			var backupId = await SeedAsync(directory);
			Databases.SetDataDirectoryForTest(directory);
			Databases.Init();
			HostMaintenance.ResetForTest();
			var original = DatabaseBackupStore.HashFile(Path.Combine(directory, "Fake.db"));
			var pending = HostMaintenance.Pending;
			var response = CliDispatcher.ExecuteAsync($"db restore Fake {backupId} --confirm");
			var accepted = await response.WaitAsync(TimeSpan.FromSeconds(2));
			Assert.Equal(L.Get("MaintenanceAccepted"), accepted.Output);
			Assert.Equal(backupId, (await pending).Value);
			Assert.Equal(L.Get("MaintenanceBusy"), (await CliDispatcher.ExecuteAsync("db unlock Fake --confirm")).Output);

			var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var drain = new TaskCompletionSource<ExitCoordinator.ExitReport>(TaskCreationOptions.RunContinuationsAsynchronously);
			var restarted = false;
			var execution = HostMaintenance.ExecutePendingAsync(async () => {
				await response;
				entered.SetResult();
				return await drain.Task;
			}, () => restarted = true);
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
			Assert.False(execution.IsCompleted);
			Assert.Equal(original, DatabaseBackupStore.HashFile(Path.Combine(directory, "Fake.db")));
			drain.SetResult(new ExitCoordinator.ExitReport(false, ["business is still running"], false));

			Assert.False(await execution);
			Assert.False(restarted);
			Assert.Equal(original, DatabaseBackupStore.HashFile(Path.Combine(directory, "Fake.db")));
			Assert.Empty(MaintenanceJournal.ListUnfinishedRestores(directory, "Fake"));
			Assert.False(HostMaintenance.Pending.IsCompleted);
			Assert.Null(HostMaintenance.ProtectedRestoreBackupId("Fake"));
			Assert.False(DataDirectoryOwnership.TryAcquire(directory, out _, out _));
		} finally {
			Cleanup(directory);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RecoveryOrRestartFailure_KeepsTheLocalMaintenanceFlowAvailable(bool failRecovery) {
		var directory = TempDirectory();
		try {
			var backupId = await SeedAsync(directory);
			Databases.SetDataDirectoryForTest(directory);
			Databases.Init();
			HostMaintenance.ResetForTest();
			Assert.Equal(L.Get("MaintenanceAccepted"), (await CliDispatcher.ExecuteAsync($"db restore Fake {backupId} --confirm")).Output);
			var attempts = 0;
			if (failRecovery) DatabaseRestoreEngine.BoundaryForTest = stage => {
				if (stage == "Ready") throw new IOException("injected before cutover");
			};

			var restarted = await HostMaintenance.ExecutePendingAsync(() => Task.FromResult(new ExitCoordinator.ExitReport(true, [], true)), () => {
				attempts++;
				return false;
			});

			Assert.False(restarted);
			Assert.Equal(failRecovery ? 0 : 1, attempts);
			Assert.True(HostMaintenance.MaintenanceOnly);
			Assert.False(HostMaintenance.Pending.IsCompleted);
			Assert.Null(HostMaintenance.ProtectedRestoreBackupId("Fake"));
			Assert.Equal(failRecovery ? "live-target" : "backup-target", ReadName(directory));
			var status = await CliDispatcher.ExecuteAsync("db status Fake");
			Assert.Contains("Fake", status.Output);
			Assert.False(status.ShouldExit);
		} finally {
			Cleanup(directory);
		}
	}

	[Fact]
	public void RestartCommand_PreservesTheEffectiveDirectoryAndProgramLocation() {
		var directory = TempDirectory();
		try {
			Databases.SetDataDirectoryForTest(directory);
			var info = Program.RestartStartInfo();

			Assert.Equal(Environment.ProcessPath, info.FileName);
			Assert.Equal(AppContext.BaseDirectory, info.WorkingDirectory);
			Assert.Equal("--data-dir", info.ArgumentList[^2]);
			Assert.Equal(directory, info.ArgumentList[^1]);
			if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) {
				Assert.Equal(typeof(Program).Assembly.Location, info.ArgumentList[0]);
			}
		} finally {
			Cleanup(directory);
		}
	}

	[Theory]
	[InlineData("restore")]
	[InlineData("recover")]
	public async Task RejectedRecovery_PreservesTheDatabaseAndDoesNotRestart(string name) {
		var directory = TempDirectory();
		try {
			await SeedAsync(directory);
			Databases.SetDataDirectoryForTest(directory);
			Databases.Init();
			HostMaintenance.ResetForTest();
			var original = DatabaseBackupStore.HashFile(Path.Combine(directory, "Fake.db"));
			var arguments = new List<string> { "db", name, "Fake", "absent", "--confirm" };
			if (name == "recover") arguments.Add("--complete");
			var response = await DatabaseCommands.ExecuteAsync(arguments);
			if (name == "restore") {
				Assert.NotEqual(L.Get("MaintenanceAccepted"), response);
				Assert.False(HostMaintenance.Busy);
				Assert.False(HostMaintenance.Pending.IsCompleted);
				Assert.Equal(original, DatabaseBackupStore.HashFile(Path.Combine(directory, "Fake.db")));
				Assert.False(HostMaintenance.MaintenanceOnly);
				return;
			}
			Assert.Equal(L.Get("MaintenanceAccepted"), response);
			var restarted = false;

			Assert.False(await HostMaintenance.ExecutePendingAsync(() => Task.FromResult(new ExitCoordinator.ExitReport(true, [], true)), () => restarted = true));

			Assert.False(restarted);
			Assert.Equal(original, DatabaseBackupStore.HashFile(Path.Combine(directory, "Fake.db")));
			Assert.True(HostMaintenance.MaintenanceOnly);
			Assert.False(HostMaintenance.Pending.IsCompleted);
			Assert.Null(HostMaintenance.ProtectedRestoreBackupId("Fake"));
		} finally {
			Cleanup(directory);
		}
	}

	[Fact]
	public async Task TwoActualHosts_KeepDirectoryOwnershipUntilTheFirstProcessExits() {
		var root = TempDirectory();
		var directory = Path.Combine(root, "data");
		Process? first = null;
		Process? second = null;
		Task<string>? firstOutput = null;
		Task<string>? firstError = null;
		Task<string>? secondOutput = null;
		Task<string>? secondError = null;
		try {
			await SeedAsync(directory);
			first = Process.Start(HostStartInfo(CloneHost(root, "first"), directory))!;
			firstError = first.StandardError.ReadToEndAsync();
			await WaitForMaintenanceConsoleAsync(first);
			firstOutput = first.StandardOutput.ReadToEndAsync();
			Assert.False(first.HasExited);
			Assert.False(DataDirectoryOwnership.TryAcquire(directory, out _, out _));
			var before = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
				.Where(path => Path.GetFileName(path) != DataDirectoryOwnership.LockFileName)
				.ToDictionary(path => Path.GetRelativePath(directory, path), DatabaseBackupStore.HashFile);

			second = Process.Start(HostStartInfo(CloneHost(root, "second"), directory))!;
			secondOutput = second.StandardOutput.ReadToEndAsync();
			secondError = second.StandardError.ReadToEndAsync();
			await second.StandardInput.WriteLineAsync("exit");
			second.StandardInput.Close();
			await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
			var rejected = await secondOutput + await secondError;
			var message = Localizer.Get(typeof(Databases).Assembly, "DataDirectoryNotOwned", CultureInfo.InvariantCulture).Split('{')[0].TrimEnd();
			Assert.Contains(message, rejected);
			Assert.NotEqual(0, second.ExitCode);
			Assert.False(first.HasExited);
			var after = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
				.Where(path => Path.GetFileName(path) != DataDirectoryOwnership.LockFileName)
				.ToDictionary(path => Path.GetRelativePath(directory, path), DatabaseBackupStore.HashFile);
			Assert.Equal(before.OrderBy(pair => pair.Key), after.OrderBy(pair => pair.Key));
			await first.StandardInput.WriteLineAsync("exit");
			first.StandardInput.Close();
			await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
			Assert.True(DataDirectoryOwnership.TryAcquire(directory, out var ownership, out var failure), failure);
			ownership!.Dispose();
		} finally {
			if (second != null) {
				if (!second.HasExited) {
					second.Kill(true);
					await second.WaitForExitAsync();
				}
				if (secondOutput != null) await secondOutput;
				if (secondError != null) await secondError;
				second.Dispose();
			}
			if (first != null) {
				if (!first.HasExited) {
					first.Kill(true);
					await first.WaitForExitAsync();
				}
				if (firstOutput != null) await firstOutput;
				if (firstError != null) await firstError;
				first.Dispose();
			}
			Cleanup(root);
		}
	}

	private static ProcessStartInfo HostStartInfo(string runtime, string directory) {
		var info = new ProcessStartInfo("dotnet") {
			WorkingDirectory = runtime,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		info.ArgumentList.Add(Path.Combine(runtime, "Pengin1011.dll"));
		info.ArgumentList.Add("--data-dir");
		info.ArgumentList.Add(directory);
		return info;
	}

	private static async Task WaitForMaintenanceConsoleAsync(Process process) {
		var expected = Localizer.Get(typeof(Databases).Assembly, "MaintenanceConsole", CultureInfo.InvariantCulture);
		using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		while (true) {
			var line = await process.StandardOutput.ReadLineAsync(budget.Token);
			Assert.NotNull(line);
			if (line.Contains(expected, StringComparison.Ordinal)) return;
		}
	}

	private static string CloneHost(string root, string name) {
		var directory = Path.Combine(root, name);
		Directory.CreateDirectory(directory);
		foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory)) {
			var file = Path.GetFileName(path);
			if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || file is "Pengin1011.deps.json" or "Pengin1011.runtimeconfig.json") {
				File.Copy(path, Path.Combine(directory, file));
			}
		}
		var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
		foreach (var runtime in new[] { RuntimeInformation.RuntimeIdentifier, platform }) {
			var source = Path.Combine(AppContext.BaseDirectory, "runtimes", runtime);
			if (!Directory.Exists(source)) continue;
			foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
				var target = Path.Combine(directory, "runtimes", runtime, Path.GetRelativePath(source, path));
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				File.Copy(path, target);
			}
		}
		return directory;
	}
}
