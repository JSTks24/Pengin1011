using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ShutdownSignalsTests {
	private const string LastWrite = "signal-final-write";

	[UnixSignalFact]
	public async Task Sigterm_StopsRealHostAndPreservesLastWriteInFinalBackup() {
		var directory = Path.Combine(Path.GetTempPath(), $"pengin_sigterm_{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		using var process = Process.Start(TestChildProcess.StartInfo("signal-probe", directory))!;
		var output = process.StandardOutput.ReadToEndAsync();
		var error = process.StandardError.ReadToEndAsync();
		using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
		try {
			var ready = Path.Combine(directory, "signal-ready.txt");
			while (!File.Exists(ready)) {
				if (process.HasExited) throw new InvalidOperationException($"Signal probe exited before ready: {await output}{await error}");
				await Task.Delay(20, budget.Token);
			}
			var acquiredWhileRunning = DataDirectoryOwnership.TryAcquire(directory, out var unexpectedOwner, out _);
			unexpectedOwner?.Dispose();
			Assert.False(acquiredWhileRunning);
			var signal = new ProcessStartInfo("kill") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
			signal.ArgumentList.Add("-TERM");
			signal.ArgumentList.Add(process.Id.ToString(CultureInfo.InvariantCulture));
			using (var sender = Process.Start(signal)!) {
				var signalError = sender.StandardError.ReadToEndAsync();
				var signalOutput = sender.StandardOutput.ReadToEndAsync();
				await sender.WaitForExitAsync(budget.Token);
				Assert.True(sender.ExitCode == 0, $"SIGTERM could not be sent: {await signalOutput}{await signalError}");
			}

			await process.WaitForExitAsync(budget.Token);

			Assert.True(process.ExitCode == 0, $"Signal probe exited with {process.ExitCode}: {await output}{await error}");
			Assert.Equal(LastWrite, File.ReadAllText(Path.Combine(directory, "signal-completed.txt")));
			var backup = Assert.Single(DatabaseBackupStore.ListCompleted(directory, "Fake", DatabaseBackupStore.RollingCategory));
			Assert.True(DatabaseBackupStore.TryVerify(directory, "Fake", backup.Category, backup.BackupId, out _, out var failure), failure);
			using (var connection = SqliteConnectionPolicy.OpenReadOnly(backup.SnapshotPath)) {
				using var command = connection.CreateCommand();
				command.CommandText = "SELECT Name FROM Items";
				Assert.Equal(LastWrite, command.ExecuteScalar());
			}
			Assert.True(DataDirectoryOwnership.TryAcquire(directory, out var nextOwner, out failure), failure);
			nextOwner!.Dispose();
		} finally {
			if (!process.HasExited) {
				process.Kill(true);
				await process.WaitForExitAsync();
			}
			await output;
			await error;
			SqliteConnection.ClearAllPools();
			Directory.Delete(directory, true);
		}
	}

	[Fact]
	public async Task FirstRequest_CancelsBusinessWithoutWaitingForShutdown_SecondRequestAllowsEscalation() {
		using var businessLifetime = new CancellationTokenSource();
		var shutdownCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var requests = 0;
		using var signals = new ShutdownSignals(() => {
			Interlocked.Increment(ref requests);
			businessLifetime.Cancel();
		}, shutdownCompleted.Task);
		var business = Task.Delay(Timeout.Infinite, businessLifetime.Token);

		Assert.True(await Task.Run(signals.RequestStop).WaitAsync(TimeSpan.FromSeconds(5)));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => business);
		Assert.False(shutdownCompleted.Task.IsCompleted);
		Assert.False(signals.RequestStop());
		Assert.Equal(1, requests);
	}

	internal static async Task<int> RunProbeAsync(string directory) {
		var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var shutdownCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		ShutdownSignals? signals = null;
		try {
			Databases.ConfigureDataDirectory(directory);
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			await using (var database = Databases.Open<FakeDbContext>()) {
				database.Items.Add(new FakeItem { Name = "before-signal" });
				await database.SaveChangesAsync();
			}
			signals = new ShutdownSignals(() => stopRequested.TrySetResult(), shutdownCompleted.Task);
			File.WriteAllText(Path.Combine(directory, "signal-ready.txt"), "ready");
			await stopRequested.Task.WaitAsync(TimeSpan.FromSeconds(60));
			await using (var database = Databases.Open<FakeDbContext>()) {
				var item = await database.Items.SingleAsync();
				item.Name = LastWrite;
				await database.SaveChangesAsync();
			}
			var report = await HostExit.StopAsync();
			if (!report.Success || !report.ResourcesSettled) return 3;
			Databases.ReleaseOwnership();
			File.WriteAllText(Path.Combine(directory, "signal-completed.txt"), LastWrite);
			return 0;
		} finally {
			shutdownCompleted.TrySetResult();
			signals?.Dispose();
			HostExit.ResetForTest();
			ExitCoordinator.ResetForTest();
			HostMaintenance.ResetForTest();
			Databases.SetDataDirectoryForTest(null);
			SqliteConnection.ClearAllPools();
		}
	}
}

file sealed class UnixSignalFactAttribute : FactAttribute {
	public UnixSignalFactAttribute() {
		if (OperatingSystem.IsWindows()) Skip = "The real SIGTERM process test requires Unix; Windows console close uses a different operating system signal.";
	}
}
