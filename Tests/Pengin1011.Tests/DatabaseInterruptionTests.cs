using System.Diagnostics;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class DatabaseInterruptionTests {
	[Theory]
	[InlineData("MarkerWritten")]
	[InlineData("SidecarsRemoved")]
	[InlineData("MainReplaced")]
	public async Task KilledRestoreBlocksAnotherProcessAndOfflineRetryRestoresTheTarget(string boundary) {
		var directory = Path.Combine(Path.GetTempPath(), "pengin_restore_process_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try {
			await TestChildProcess.RunArgumentsAsync("db-seed", directory);
			var backup = File.ReadAllText(Path.Combine(directory, "backup-id.txt"));
			var signal = Path.Combine(directory, "boundary.txt");
			using (var process = Process.Start(TestChildProcess.StartInfo("db-interrupt", directory, backup, boundary, signal))!) {
				var output = process.StandardOutput.ReadToEndAsync();
				var error = process.StandardError.ReadToEndAsync();
				using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
				try {
					while (!File.Exists(signal)) {
						Assert.False(process.HasExited, process.HasExited ? await output + await error : "");
						await Task.Delay(25, budget.Token);
					}
					Assert.False(DataDirectoryOwnership.TryAcquire(directory, out var competing, out _));
					competing?.Dispose();
				} finally {
					if (!process.HasExited) process.Kill();
					await process.WaitForExitAsync();
					await output;
					await error;
				}
			}
			await TestChildProcess.RunArgumentsAsync("db-expect-blocked", directory);
			await TestChildProcess.RunArgumentsAsync("db-offline", "db", "restore", "Fake", backup, "--data-dir", directory, "--confirm");
			await TestChildProcess.RunArgumentsAsync("db-read", directory, "backup-target");
			Assert.False(DatabaseMaintenance.Exists(directory, "Fake"));
		} finally {
			Directory.Delete(directory, true);
		}
	}
}
