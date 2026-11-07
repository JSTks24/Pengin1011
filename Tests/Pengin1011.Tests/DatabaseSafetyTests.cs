using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class DatabaseSafetyTests : IDisposable {
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "pengin_database_" + Guid.NewGuid().ToString("N"));

	public DatabaseSafetyTests() {
		Directory.CreateDirectory(_directory);
		DatabaseTestSetup.SetDirectory(_directory);
		Databases.Init();
	}

	public void Dispose() {
		DatabaseRestoreEngine.BoundaryForTest = null;
		DatabasePrepareEngine.IntentWrittenForTest = null;
		DatabaseTestSetup.SetDirectory(null);
		SqliteConnection.ClearAllPools();
		Directory.Delete(_directory, true);
	}

	private async Task<DatabaseBackupInfo> SeedAsync() {
		await Databases.PrepareAsync<FakeDbContext>();
		await using (var database = Databases.Open<FakeDbContext>()) {
			database.Items.Add(new FakeItem { Name = "snapshot" });
			await database.SaveChangesAsync();
		}
		Assert.True((await Databases.BackupNowAsync()).Success);
		var backup = Assert.Single(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory));
		await using (var database = Databases.Open<FakeDbContext>()) {
			(await database.Items.SingleAsync()).Name = "original";
			await database.SaveChangesAsync();
		}
		return backup;
	}

	private static string ReadName(string path) {
		using var connection = SqliteConnectionPolicy.OpenReadOnly(path);
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT Name FROM Items";
		return (string)command.ExecuteScalar()!;
	}

	[Fact]
	public async Task BackupMetadataOmitsUnusedFieldsAndExistingExtraFieldsStillRestore() {
		var backup = await SeedAsync();
		var path = Path.Combine(Path.GetDirectoryName(backup.SnapshotPath)!, DatabaseBackupStore.MetadataFileName);
		var metadata = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
		Assert.False(metadata.ContainsKey("OperationId"));
		Assert.False(metadata.ContainsKey("ApplicationCommit"));
		metadata["OperationId"] = "previous-operation";
		metadata["ApplicationCommit"] = "previous-commit";
		File.WriteAllText(path, metadata.ToJsonString());
		Assert.True(DatabaseBackupStore.TryVerify(_directory, "Fake", backup.Category, backup.BackupId, out _, out _));
		var result = await DatabaseRestoreEngine.RestoreAsync(_directory, "Fake", backup.BackupId, CancellationToken.None);
		Assert.Equal(RestoreOutcome.Completed, result.Outcome);
		Assert.Equal("snapshot", ReadName(Path.Combine(_directory, "Fake.db")));
	}

	[Fact]
	public async Task RestorePreservesOriginalAndClearsPendingMarkerOnlyAfterVerification() {
		var backup = await SeedAsync();
		DatabaseMaintenance.Write(_directory, "Fake", "migration");
		var result = await DatabaseRestoreEngine.RestoreAsync(_directory, "Fake", backup.BackupId, CancellationToken.None);
		Assert.Equal(RestoreOutcome.Completed, result.Outcome);
		Assert.False(DatabaseMaintenance.Exists(_directory, "Fake"));
		Assert.Equal("snapshot", ReadName(Path.Combine(_directory, "Fake.db")));
		var original = Path.Combine(_directory, "backup", "Fake", "restore", result.OperationId!, "original", "Fake.db");
		Assert.Equal("original", ReadName(original));
	}

	[Theory]
	[InlineData("MarkerWritten")]
	[InlineData("SidecarsRemoved")]
	[InlineData("MainReplaced")]
	public async Task InterruptedRestoreBlocksBusinessAndCanBeRepeatedFromTheBackup(string boundary) {
		var backup = await SeedAsync();
		DatabaseRestoreEngine.BoundaryForTest = value => {
			if (value == boundary) throw new IOException("injected interruption");
		};
		var result = await DatabaseRestoreEngine.RestoreAsync(_directory, "Fake", backup.BackupId, CancellationToken.None);
		Assert.Equal(RestoreOutcome.Failed, result.Outcome);
		Assert.True(DatabaseMaintenance.Exists(_directory, "Fake"));
		Assert.Throws<InvalidOperationException>(() => Databases.Open<FakeDbContext>());
		Assert.False((await Databases.BackupNowAsync()).Success);
		DatabaseTestSetup.SetDirectory(_directory);
		await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
		Assert.Equal(DatabaseState.MaintenanceRequired, Databases.StateOf<FakeDbContext>());
		DatabaseRestoreEngine.BoundaryForTest = null;
		Assert.Equal(RestoreOutcome.Completed, (await DatabaseRestoreEngine.RestoreAsync(_directory, "Fake", backup.BackupId, CancellationToken.None)).Outcome);
		await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
		DatabaseTestSetup.SetDirectory(_directory);
		await Databases.PrepareAsync<FakeDbContext>();
		Assert.Equal("snapshot", ReadName(Path.Combine(_directory, "Fake.db")));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task InvalidSourceOrOriginalCopyFailureDoesNotChangeTheLiveDatabase(bool corruptBackup) {
		var backup = await SeedAsync();
		if (corruptBackup) File.AppendAllText(backup.SnapshotPath, "corruption");
		RestoreResult result;
		using (var locked = corruptBackup ? null : new FileStream(Path.Combine(_directory, "Fake.db"), FileMode.Open, FileAccess.Read, FileShare.None)) {
			result = await DatabaseRestoreEngine.RestoreAsync(_directory, "Fake", backup.BackupId, CancellationToken.None);
		}
		Assert.NotEqual(RestoreOutcome.Completed, result.Outcome);
		Assert.Equal("original", ReadName(Path.Combine(_directory, "Fake.db")));
		Assert.False(DatabaseMaintenance.Exists(_directory, "Fake"));
	}

	[Fact]
	public async Task RestoreRemovesUnrelatedSidecarsWithoutOpeningThem() {
		var backup = await SeedAsync();
		var live = Path.Combine(_directory, "Fake.db");
		foreach (var suffix in DatabaseRestoreEngine.FileSuffixes.Skip(1)) File.WriteAllText(live + suffix, "unrelated");
		var result = await DatabaseRestoreEngine.RestoreAsync(_directory, "Fake", backup.BackupId, CancellationToken.None);
		Assert.Equal(RestoreOutcome.Completed, result.Outcome);
		Assert.Equal("snapshot", ReadName(live));
		foreach (var suffix in DatabaseRestoreEngine.FileSuffixes.Skip(1)) {
			var original = Path.Combine(_directory, "backup", "Fake", "restore", result.OperationId!, "original", "Fake.db" + suffix);
			Assert.Equal("unrelated", File.ReadAllText(original));
		}
	}

	[Fact]
	public async Task UpgradePreservesV1RowsAndCreatesAVerifiablePreMigrationSnapshot() {
		var path = Path.Combine(_directory, "Fake.db");
		var options = new DbContextOptionsBuilder<FakeDbContext>().UseSqlite(SqliteConnectionPolicy.ForCreatingDatabase(path)).Options;
		await using (var context = new FakeDbContext(options)) {
			var first = context.Database.GetMigrations().First();
			await context.GetService<IMigrator>().MigrateAsync(first);
			await context.Database.ExecuteSqlRawAsync("INSERT INTO Items (Id, Name) VALUES (1, 'v1')");
		}
		await Databases.PrepareAsync<FakeDbContext>();
		Assert.Equal("v1", ReadName(path));
		Assert.Equal(2, DatabaseBackupStore.ReadHistory(path).Count);
		var backup = Assert.Single(Databases.ListBackups("Fake", DatabaseBackupStore.PreMigrationCategory));
		Assert.Single(backup.History);
		Assert.True(DatabaseBackupStore.TryVerify(_directory, "Fake", backup.Category, backup.BackupId, out _, out _));
		Assert.False(DatabaseMaintenance.Exists(_directory, "Fake"));
	}

	[Fact]
	public async Task FailedSqlMigrationPreservesRowsSnapshotAndPendingMarker() {
		var path = Path.Combine(_directory, "Fake.db");
		var options = new DbContextOptionsBuilder<FakeDbContext>().UseSqlite(SqliteConnectionPolicy.ForCreatingDatabase(path)).Options;
		await using (var context = new FakeDbContext(options)) {
			await context.GetService<IMigrator>().MigrateAsync(context.Database.GetMigrations().First());
			await context.Database.ExecuteSqlRawAsync("INSERT INTO Items (Id, Name) VALUES (1, 'v1')");
			await context.Database.ExecuteSqlRawAsync("CREATE INDEX IX_Items_Name ON Items (Name)");
		}
		await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
		Assert.True(DatabaseMaintenance.Exists(_directory, "Fake"));
		Assert.Single(DatabaseBackupStore.ReadHistory(path));
		Assert.Equal("v1", ReadName(path));
		Assert.Throws<InvalidOperationException>(() => Databases.Open<FakeDbContext>());
		var backup = Assert.Single(Databases.ListBackups("Fake", DatabaseBackupStore.PreMigrationCategory));
		Assert.True(DatabaseBackupStore.TryVerify(_directory, "Fake", backup.Category, backup.BackupId, out _, out _));
	}

	[Fact]
	public async Task FreshMigrationFailureKeepsTheMarkerAndDoesNotSilentlyRetry() {
		DatabasePrepareEngine.IntentWrittenForTest = _ => throw new IOException("injected migration failure");
		await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
		Assert.True(DatabaseMaintenance.Exists(_directory, "Fake"));
		DatabasePrepareEngine.IntentWrittenForTest = null;
		await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
		Assert.Equal(DatabaseState.Failed, Databases.StateOf<FakeDbContext>());
		DatabaseTestSetup.SetDirectory(_directory);
		await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
		Assert.Equal(DatabaseState.MaintenanceRequired, Databases.StateOf<FakeDbContext>());
		Assert.Empty(Databases.ListBackups("Fake", DatabaseBackupStore.PreMigrationCategory));
	}

	[Fact]
	public async Task CancellationAfterMarkerPublicationKeepsStartupBlocked() {
		var backup = await SeedAsync();
		using var cancellation = new CancellationTokenSource();
		DatabaseRestoreEngine.BoundaryForTest = boundary => {
			if (boundary == "MarkerWritten") cancellation.Cancel();
		};
		Assert.Equal(RestoreOutcome.Failed, (await DatabaseRestoreEngine.RestoreAsync(_directory, "Fake", backup.BackupId, cancellation.Token)).Outcome);
		Assert.True(DatabaseMaintenance.Exists(_directory, "Fake"));
		Assert.Equal("original", ReadName(Path.Combine(_directory, "Fake.db")));
	}

	[Theory]
	[InlineData("../Fake")]
	[InlineData("missing-backup")]
	public async Task InvalidBackupSelectionLeavesDataUnchanged(string backupId) {
		await SeedAsync();
		var result = await DatabaseRestoreEngine.RestoreAsync(_directory, "Fake", backupId, CancellationToken.None);
		Assert.Equal(RestoreOutcome.Rejected, result.Outcome);
		Assert.False(DatabaseMaintenance.Exists(_directory, "Fake"));
		Assert.Equal("original", ReadName(Path.Combine(_directory, "Fake.db")));
	}

	[Fact]
	public async Task UntrackedBusinessTablesCannotBeRestoredAsAManagedDatabase() {
		await SeedAsync();
		var source = Path.Combine(_directory, "untracked.db");
		using (var connection = new SqliteConnection(SqliteConnectionPolicy.ForCreatingDatabase(source))) {
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = "CREATE TABLE Business (Id INTEGER PRIMARY KEY)";
			command.ExecuteNonQuery();
		}
		Assert.True(DatabaseBackupStore.TryCreate(_directory, "Fake", DatabaseBackupStore.RollingCategory, source, [], out var backup, out _));
		Assert.Equal(RestoreOutcome.Rejected, (await DatabaseRestoreEngine.RestoreAsync(_directory, "Fake", backup!, CancellationToken.None)).Outcome);
		Assert.Equal("original", ReadName(Path.Combine(_directory, "Fake.db")));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PendingPreparationRespondsToBudgetOrShutdown(bool shutdown) {
		var originalBudget = DatabaseService.PrepareBudget;
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		DatabaseService.PrepareBudget = shutdown ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(100);
		DatabasePrepareEngine.IntentWrittenForTest = async ct => {
			entered.TrySetResult();
			await Task.Delay(Timeout.Infinite, ct);
		};
		try {
			var prepare = Databases.PrepareAsync<FakeDbContext>();
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			if (shutdown) await Databases.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
			await Assert.ThrowsAsync<InvalidOperationException>(() => prepare.WaitAsync(TimeSpan.FromSeconds(5)));
			Assert.True(DatabaseMaintenance.Exists(_directory, "Fake"));
		} finally {
			DatabaseService.PrepareBudget = originalBudget;
		}
	}
}
