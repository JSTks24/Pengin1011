using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class DatabaseUpgradeTests : IDisposable {
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"pengin1011_upgrade_{Guid.NewGuid():N}");
	private readonly TimeSpan _prepareBudget = DatabaseService.PrepareBudget;

	public DatabaseUpgradeTests() {
		Directory.CreateDirectory(_directory);
		Databases.SetDataDirectoryForTest(_directory);
	}

	public void Dispose() {
		DatabasePrepareEngine.IntentWrittenForTest = null;
		DatabaseRestoreEngine.BoundaryForTest = null;
		DatabaseService.PrepareBudget = _prepareBudget;
		Databases.SetDataDirectoryForTest(null);
		SqliteConnection.ClearAllPools();
		Directory.Delete(_directory, true);
	}

	private async Task<(string First, string Latest)> SeedV1Async() {
		var path = Path.Combine(_directory, "Fake.db");
		var options = new DbContextOptionsBuilder<FakeDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
		await using var context = new FakeDbContext(options);
		var migrations = context.Database.GetMigrations().ToArray();
		Assert.Equal(2, migrations.Length);
		Assert.EndsWith("_Init", migrations[0]);
		Assert.EndsWith("_AddNameIndex", migrations[1]);
		await context.GetService<IMigrator>().MigrateAsync(migrations[0]);
		await context.Database.ExecuteSqlRawAsync("INSERT INTO Items (Id, Name, Note) VALUES (41, 'existing-v1', 'kept')");
		Assert.Equal([migrations[0]], DatabaseBackupStore.ReadHistory(path));
		Assert.False(HasNameIndex(path));
		return (migrations[0], migrations[1]);
	}

	private async Task<(MaintenanceJournal.MigrationIntent Intent, DatabaseBackupInfo Backup)> InterruptUpgradeAsync() {
		await SeedV1Async();
		Databases.Init();
		DatabasePrepareEngine.IntentWrittenForTest = _ => throw new IOException("injected migration interruption");
		try {
			await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
		} finally {
			DatabasePrepareEngine.IntentWrittenForTest = null;
		}
		return (MaintenanceJournal.ReadMigrationIntent(_directory, "Fake")!, Assert.Single(Databases.ListBackups("Fake", DatabaseBackupStore.PreMigrationCategory)));
	}

	private static bool HasNameIndex(string path) {
		using var connection = SqliteConnectionPolicy.OpenReadOnly(path);
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_Items_Name'";
		return Convert.ToInt64(command.ExecuteScalar()) == 1;
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InterruptedUpgradeCanRestoreV1AndPrepareAgain(bool interruptAfterCompleted) {
		var interrupted = await InterruptUpgradeAsync();
		if (interruptAfterCompleted) {
			DatabaseRestoreEngine.BoundaryForTest = stage => {
				if (stage == "Completed") throw new IOException("injected interruption after durable completion");
			};
		}
		var restored = await Databases.RestoreAsync("Fake", interrupted.Backup.BackupId);
		DatabaseRestoreEngine.BoundaryForTest = null;
		var record = MaintenanceJournal.ReadRestoreRecord(_directory, "Fake", restored.OperationId!)!;
		Assert.Equal("Completed", record.State);
		Assert.Equal(interrupted.Intent.OperationId, record.OriginalMigrationOperationId);
		if (interruptAfterCompleted) {
			Assert.Equal(RestoreOutcome.Failed, restored.Outcome);
			Assert.Equal(interrupted.Intent.OperationId, MaintenanceJournal.ReadMigrationIntent(_directory, "Fake")?.OperationId);
			var completed = Databases.CompleteRestore("Fake", record.OperationId);
			Assert.True(completed.Success, completed.Reason);
		} else {
			Assert.Equal(RestoreOutcome.Completed, restored.Outcome);
		}
		Assert.Null(MaintenanceJournal.ReadMigrationIntent(_directory, "Fake"));
		Assert.False(HasNameIndex(Path.Combine(_directory, "Fake.db")));

		await Databases.PrepareAsync<FakeDbContext>();

		Assert.True(HasNameIndex(Path.Combine(_directory, "Fake.db")));
		await using var current = Databases.Open<FakeDbContext>();
		Assert.Equal("existing-v1", (await current.Items.SingleAsync()).Name);
		Assert.Equal(2, (await current.Database.GetAppliedMigrationsAsync()).Count());
	}

	[Fact]
	public async Task RollbackPreservesTheInterruptedUpgradeIntent() {
		var interrupted = await InterruptUpgradeAsync();
		DatabaseRestoreEngine.BoundaryForTest = stage => {
			if (stage == "SidecarsMoved") throw new IOException("injected cutover interruption");
		};
		var restored = await Databases.RestoreAsync("Fake", interrupted.Backup.BackupId);
		DatabaseRestoreEngine.BoundaryForTest = null;
		Assert.Equal(RestoreOutcome.Failed, restored.Outcome);

		var rollback = Databases.RollbackRestore("Fake", restored.OperationId!);

		Assert.True(rollback.Success, rollback.Reason);
		Assert.Equal(interrupted.Intent.OperationId, MaintenanceJournal.ReadMigrationIntent(_directory, "Fake")?.OperationId);
		await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
	}

	[Fact]
	public async Task OldCompletedRestoreCannotClearANewUpgradeIntent() {
		var interrupted = await InterruptUpgradeAsync();
		var restored = await Databases.RestoreAsync("Fake", interrupted.Backup.BackupId);
		Assert.Equal(RestoreOutcome.Completed, restored.Outcome);
		var record = MaintenanceJournal.ReadRestoreRecord(_directory, "Fake", restored.OperationId!)!;
		DatabasePrepareEngine.IntentWrittenForTest = _ => throw new IOException("injected later upgrade interruption");
		try {
			await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
		} finally {
			DatabasePrepareEngine.IntentWrittenForTest = null;
		}
		var newer = MaintenanceJournal.ReadMigrationIntent(_directory, "Fake")!;
		Assert.NotEqual(record.OriginalMigrationOperationId, newer.OperationId);
		Assert.Equal(record.TargetSha256, DatabaseBackupStore.HashFile(Path.Combine(_directory, "Fake.db")));

		var completed = Databases.CompleteRestore("Fake", record.OperationId);

		Assert.False(completed.Success);
		Assert.Equal(newer.OperationId, MaintenanceJournal.ReadMigrationIntent(_directory, "Fake")?.OperationId);
		Assert.Equal(DatabaseState.MaintenanceRequired, Databases.GetStatus("Fake").State);
	}

	[Theory]
	[InlineData("Completed", false)]
	[InlineData("RolledBack", false)]
	[InlineData("Failed", true)]
	public async Task LegacyTerminalRecordsRemainHistoryWhileFailedRecordsBlockPreparation(string state, bool blocked) {
		var versions = await SeedV1Async();
		const string operation = "legacy-operation";
		var path = Path.Combine(MaintenanceJournal.OperationDirectory(_directory, "Fake", operation), MaintenanceJournal.RecordFileName);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var json = $$"""
			{
			  "OperationId": "{{operation}}",
			  "Database": "Fake",
			  "SourceBackupId": "legacy-backup",
			  "OriginalDatabasePath": "Fake.db",
			  "OriginalSidecars": [],
			  "TargetHistory": "{{versions.First}}",
			  "State": "{{state}}",
			  "StartedUtc": "2026-10-14T00:00:00Z",
			  "CompletedUtc": "2026-10-14T00:00:01Z",
			  "Stages": ["CutoverStarted"]
			}
			""";
		File.WriteAllText(path, json);
		Databases.Init();
		if (blocked) {
			await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
			Assert.Equal(DatabaseState.MaintenanceRequired, Databases.GetStatus("Fake").State);
			Assert.False(HasNameIndex(Path.Combine(_directory, "Fake.db")));
		} else {
			await Databases.PrepareAsync<FakeDbContext>();
			Assert.Equal(DatabaseState.Prepared, Databases.StateOf<FakeDbContext>());
			Assert.True(HasNameIndex(Path.Combine(_directory, "Fake.db")));
		}
		Assert.False(Databases.CompleteRestore("Fake", operation).Success);
		Assert.Equal(json, File.ReadAllText(path));
	}

	[Fact]
	public async Task D03_RealV1UpgradesToV2_AndPreMigrationSnapshotPreservesV1DataAndSchema() {
		var versions = await SeedV1Async();
		Databases.Init();

		await Databases.PrepareAsync<FakeDbContext>();

		await using (var current = Databases.Open<FakeDbContext>()) {
			var row = await current.Items.SingleAsync();
			Assert.Equal(41, row.Id);
			Assert.Equal("existing-v1", row.Name);
			Assert.Equal("kept", row.Note);
			Assert.Equal([versions.First, versions.Latest], await current.Database.GetAppliedMigrationsAsync());
		}
		Assert.True(HasNameIndex(Path.Combine(_directory, "Fake.db")));
		var backup = Assert.Single(Databases.ListBackups("Fake", DatabaseBackupStore.PreMigrationCategory));
		Assert.Equal([versions.First], backup.History);
		Assert.False(HasNameIndex(backup.SnapshotPath));
		Assert.Equal([versions.First], DatabaseBackupStore.ReadHistory(backup.SnapshotPath));
		using (var snapshot = SqliteConnectionPolicy.OpenReadOnly(backup.SnapshotPath)) {
			using var command = snapshot.CreateCommand();
			command.CommandText = "SELECT Name || ':' || Note FROM Items WHERE Id=41";
			Assert.Equal("existing-v1:kept", command.ExecuteScalar());
		}
		Assert.True(DatabaseBackupStore.TryVerify(_directory, "Fake", DatabaseBackupStore.PreMigrationCategory, backup.BackupId, out _, out var failure), failure);
		Assert.Null(MaintenanceJournal.ReadMigrationIntent(_directory, "Fake"));
		Assert.Empty(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory));
		await Databases.PrepareAsync<FakeDbContext>();
		Assert.Single(Databases.ListBackups("Fake", DatabaseBackupStore.PreMigrationCategory));
	}

	[Fact]
	public async Task D06_WaitersJoiningAfterRealIntentWritten_ShareTheRunningUpgrade() {
		await SeedV1Async();
		Databases.Init();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var visits = 0;
		DatabasePrepareEngine.IntentWrittenForTest = async ct => {
			Interlocked.Increment(ref visits);
			entered.TrySetResult();
			await release.Task.WaitAsync(ct);
		};
		var first = Databases.PrepareAsync<FakeDbContext>();
		Task? second = null;
		try {
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.NotNull(MaintenanceJournal.ReadMigrationIntent(_directory, "Fake"));
			second = Databases.PrepareAsync<FakeDbContext>();
			Assert.False(second.IsCompleted);
			using var cancellation = new CancellationTokenSource();
			var cancelled = Databases.PrepareAsync<FakeDbContext>(cancellation.Token);
			cancellation.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
			Assert.False(first.IsCompleted);
			Assert.False(second.IsCompleted);
		} finally {
			release.TrySetResult();
			await first;
			if (second != null) await second;
		}
		Assert.Equal(1, visits);
		Assert.Equal(DatabaseState.Prepared, Databases.StateOf<FakeDbContext>());
		Assert.Single(Databases.ListBackups("Fake", DatabaseBackupStore.PreMigrationCategory));
	}

	[Fact]
	public async Task D06_DistinctContextsPreparingTogether_ShareDirectoryOwnership() {
		Databases.Init();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var visits = 0;
		DatabasePrepareEngine.IntentWrittenForTest = async ct => {
			if (Interlocked.Increment(ref visits) != 1) return;
			entered.TrySetResult();
			await release.Task.WaitAsync(ct);
		};
		var first = Databases.PrepareAsync<FakeDbContext>();
		Task? other = null;
		try {
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			other = Databases.PrepareAsync<OtherDbContext>();
			Assert.False(other.IsCompleted);
		} finally {
			release.TrySetResult();
			await first;
			if (other != null) await other;
		}
		Assert.Equal(2, visits);
		Assert.Equal(DatabaseState.Prepared, Databases.StateOf<FakeDbContext>());
		Assert.Equal(DatabaseState.Prepared, Databases.StateOf<OtherDbContext>());
		await using var fake = Databases.Open<FakeDbContext>();
		await using var second = Databases.Open<OtherDbContext>();
		fake.Items.Add(new FakeItem { Name = "two-contexts" });
		second.Things.Add(new OtherItem());
		await fake.SaveChangesAsync();
		await second.SaveChangesAsync();
		Assert.Equal(1, await fake.Items.CountAsync());
		Assert.Equal(1, await second.Things.CountAsync());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task D06_PrepareOperation_RespondsToItsOwnBudgetAndHostShutdown(bool shutdown) {
		DatabaseService.PrepareBudget = shutdown ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(2);
		Databases.Init();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		CancellationToken operationToken = default;
		DatabasePrepareEngine.IntentWrittenForTest = ct => {
			operationToken = ct;
			entered.TrySetResult();
			return Task.Delay(Timeout.Infinite, ct);
		};
		var preparing = Databases.PrepareAsync<FakeDbContext>();
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task<BackupResult>? stopping = shutdown ? Databases.ShutdownAsync() : null;

		await Assert.ThrowsAsync<InvalidOperationException>(() => preparing.WaitAsync(TimeSpan.FromSeconds(5)));
		if (stopping != null) await stopping.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.True(operationToken.IsCancellationRequested);
		Assert.Equal(DatabaseState.Failed, Databases.StateOf<FakeDbContext>());
		Assert.NotNull(MaintenanceJournal.ReadMigrationIntent(_directory, "Fake"));
		Assert.False(DataDirectoryOwnership.TryAcquire(_directory, out var owner, out var failure));
		Assert.Null(owner);
		Assert.NotNull(failure);
	}
}
