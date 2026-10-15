using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class DatabaseSafetyTests : IDisposable {
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "pengin_safety_" + Guid.NewGuid().ToString("N"));

	public DatabaseSafetyTests() {
		Databases.SetDataDirectoryForTest(_directory);
		Databases.Init();
	}

	public void Dispose() {
		DatabaseRestoreEngine.BoundaryForTest = null;
		MaintenanceJournal.BeforePublishForTest = null;
		DatabasePrepareEngine.IntentWrittenForTest = null;
		Databases.SetDataDirectoryForTest(null);
		Directory.Delete(_directory, true);
	}

	private async Task<string> SeedAsync() {
		await Databases.PrepareAsync<FakeDbContext>();
		await using (var db = Databases.Open<FakeDbContext>()) {
			db.Items.Add(new FakeItem { Name = "target-A" });
			await db.SaveChangesAsync();
		}
		await Databases.BackupNowAsync();
		var backup = Assert.Single(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory));
		await using (var db = Databases.Open<FakeDbContext>()) {
			db.Items.Add(new FakeItem { Name = "live-B" });
			await db.SaveChangesAsync();
		}
		return backup.BackupId;
	}

	private string[] Names() {
		using var connection = SqliteConnectionPolicy.OpenReadOnly(Path.Combine(_directory, "Fake.db"));
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT Name FROM Items ORDER BY Id";
		using var reader = command.ExecuteReader();
		var names = new List<string>();
		while (reader.Read()) names.Add(reader.GetString(0));
		return names.ToArray();
	}

	private async Task<string> InterruptAsync(string backupId, string boundary) {
		DatabaseRestoreEngine.BoundaryForTest = stage => {
			if (stage == boundary) throw new IOException("injected boundary failure");
		};
		try {
			var result = await Databases.RestoreAsync("Fake", backupId);
			Assert.Equal(RestoreOutcome.Failed, result.Outcome);
			Assert.NotNull(result.OperationId);
			return result.OperationId;
		} finally {
			DatabaseRestoreEngine.BoundaryForTest = null;
		}
	}

	[Fact]
	public async Task NormalRestoreCopiesStagingOnce() {
		var backupId = await SeedAsync();
		var copies = 0;
		DatabaseRestoreEngine.BoundaryForTest = stage => {
			if (stage == "StagingCopied") copies++;
		};
		var result = await Databases.RestoreAsync("Fake", backupId);
		Assert.Equal(RestoreOutcome.Completed, result.Outcome);
		Assert.Equal(1, copies);
		Assert.Equal(["target-A"], Names());
	}

	[Fact]
	public async Task CompleteRebuildsCorruptedStagingFromTheVerifiedBackup() {
		var operation = await InterruptAsync(await SeedAsync(), "Ready");
		var staging = Path.Combine(MaintenanceJournal.OperationDirectory(_directory, "Fake", operation), MaintenanceJournal.StagingFileName);
		File.WriteAllBytes(staging, [1, 2, 3]);
		var copies = 0;
		DatabaseRestoreEngine.BoundaryForTest = stage => {
			if (stage == "StagingCopied") copies++;
		};
		var result = Databases.CompleteRestore("Fake", operation);
		Assert.True(result.Success, result.Reason);
		Assert.Equal(1, copies);
		Assert.Equal(["target-A"], Names());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CompleteRefusesMissingOrCorruptedSourceBackup(bool corrupt) {
		var backupId = await SeedAsync();
		var operation = await InterruptAsync(backupId, "Ready");
		var snapshot = DatabaseBackupStore.SnapshotPath(_directory, "Fake", DatabaseBackupStore.RollingCategory, backupId);
		if (corrupt) File.WriteAllBytes(snapshot, [1, 2, 3]);
		else File.Delete(snapshot);
		var path = Path.Combine(_directory, "Fake.db");
		var before = DatabaseBackupStore.HashFile(path);
		var result = Databases.CompleteRestore("Fake", operation);
		Assert.False(result.Success);
		Assert.Equal(before, DatabaseBackupStore.HashFile(path));
		Assert.Equal(["target-A", "live-B"], Names());
		Assert.Equal("Ready", MaintenanceJournal.ReadRestoreRecord(_directory, "Fake", operation)!.State);
		Assert.NotEmpty(MaintenanceJournal.ListUnfinishedRestores(_directory, "Fake"));
	}

	[Theory]
	[InlineData("Ready")]
	[InlineData("CutoverStarted")]
	[InlineData("SidecarsMoved")]
	public async Task CancellationAtActualBoundaryPreservesTheRecordedPhaseForComplete(string boundary) {
		var backupId = await SeedAsync();
		using var cancellation = new CancellationTokenSource();
		DatabaseRestoreEngine.BoundaryForTest = stage => {
			if (stage == boundary) cancellation.Cancel();
		};
		var result = await Databases.RestoreAsync("Fake", backupId, cancellation.Token);
		DatabaseRestoreEngine.BoundaryForTest = null;
		Assert.Equal(RestoreOutcome.Failed, result.Outcome);
		Assert.NotNull(result.OperationId);
		Assert.Equal(boundary == "SidecarsMoved" ? "CutoverStarted" : boundary,
			MaintenanceJournal.ReadRestoreRecord(_directory, "Fake", result.OperationId)!.State);
		Assert.Equal(["target-A", "live-B"], Names());
		var completed = Databases.CompleteRestore("Fake", result.OperationId);
		Assert.True(completed.Success, completed.Reason);
		Assert.Equal(["target-A"], Names());
	}

	[Theory]
	[InlineData("Ready")]
	[InlineData("CutoverStarted")]
	[InlineData("SidecarsMoved")]
	[InlineData("MainReplaced")]
	public async Task CompleteObtainsTargetDataAfterActualInterruption(string boundary) {
		var backupId = await SeedAsync();
		var operation = await InterruptAsync(backupId, boundary);
		await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
		var result = Databases.CompleteRestore("Fake", operation);
		Assert.True(result.Success, result.Reason);
		Assert.Equal(["target-A"], Names());
		Assert.Empty(MaintenanceJournal.ListUnfinishedRestores(_directory, "Fake"));
	}

	[Fact]
	public async Task QuarantineCopyFailureCannotOverwriteTheIntactOriginal() {
		var backupId = await SeedAsync();
		var path = Path.Combine(_directory, "Fake.db");
		var before = DatabaseBackupStore.HashFile(path);
		FileStream? locked = null;
		DatabaseRestoreEngine.BoundaryForTest = stage => {
			if (stage == "BeforeQuarantineCopy") locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
		};
		RestoreResult failed;
		try {
			failed = await Databases.RestoreAsync("Fake", backupId);
		} finally {
			locked?.Dispose();
			DatabaseRestoreEngine.BoundaryForTest = null;
		}
		Assert.Equal(RestoreOutcome.Failed, failed.Outcome);
		Assert.True(Databases.RollbackRestore("Fake", failed.OperationId!).Success);
		Assert.Equal(before, DatabaseBackupStore.HashFile(path));
		Assert.Equal(["target-A", "live-B"], Names());
	}

	[Fact]
	public async Task MissingOriginalDatabaseIsRestoredAsAbsentOnRollback() {
		var backupId = await SeedAsync();
		var path = Path.Combine(_directory, "Fake.db");
		File.Delete(path);
		var operation = await InterruptAsync(backupId, "MainReplaced");
		Assert.True(File.Exists(path));
		Assert.True(Databases.RollbackRestore("Fake", operation).Success);
		Assert.All(DatabaseRestoreEngine.FileSuffixes, suffix => Assert.False(File.Exists(path + suffix)));
	}

	[Theory]
	[InlineData("{")]
	[InlineData("null")]
	[InlineData("{}")]
	public async Task InvalidRestoreRecordBlocksPrepareAndAppearsInStatus(string json) {
		var operation = await InterruptAsync(await SeedAsync(), "CutoverStarted");
		var path = Path.Combine(MaintenanceJournal.OperationDirectory(_directory, "Fake", operation), MaintenanceJournal.RecordFileName);
		File.WriteAllText(path, json);
		await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
		Assert.Equal(DatabaseState.MaintenanceRequired, Databases.GetStatus("Fake").State);
		Assert.False(Databases.CompleteRestore("Fake", operation).Success);
		Assert.False(Databases.RollbackRestore("Fake", operation).Success);
	}

	[Fact]
	public async Task NullOriginalFileEntryBlocksPrepareAndAppearsInStatus() {
		var operation = await InterruptAsync(await SeedAsync(), "CutoverStarted");
		var record = MaintenanceJournal.ReadRestoreRecord(_directory, "Fake", operation)!;
		MaintenanceJournal.WriteRestoreRecord(_directory, "Fake", record with { OriginalFiles = [null!, .. record.OriginalFiles.Skip(1)] });

		await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
		Assert.Equal(DatabaseState.MaintenanceRequired, Databases.GetStatus("Fake").State);
		Assert.False(Databases.CompleteRestore("Fake", operation).Success);
		Assert.False(Databases.RollbackRestore("Fake", operation).Success);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(2)]
	public async Task CompletedV2RecordWithoutOriginalProofIsNotTreatedAsLegacyHistory(int version) {
		var backup = await SeedAsync();
		var restored = await Databases.RestoreAsync("Fake", backup);
		Assert.Equal(RestoreOutcome.Completed, restored.Outcome);
		var record = MaintenanceJournal.ReadRestoreRecord(_directory, "Fake", restored.OperationId!)!;
		MaintenanceJournal.WriteRestoreRecord(_directory, "Fake", record with { FormatVersion = version, OriginalFiles = [] });

		await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
		Assert.Equal(DatabaseState.MaintenanceRequired, Databases.GetStatus("Fake").State);
		Assert.False(Databases.CompleteRestore("Fake", record.OperationId).Success);
	}

	[Fact]
	public async Task FailedRecordPublicationPreservesThePreviousValidRecord() {
		var operation = await InterruptAsync(await SeedAsync(), "Ready");
		var record = MaintenanceJournal.ReadRestoreRecord(_directory, "Fake", operation)!;
		MaintenanceJournal.BeforePublishForTest = (path, temporary) => {
			Assert.True(File.Exists(temporary));
			Assert.Contains("CutoverStarted", File.ReadAllText(temporary));
			Assert.Contains("Ready", File.ReadAllText(path));
			throw new IOException("injected atomic publication failure");
		};
		Assert.Throws<IOException>(() => MaintenanceJournal.WriteRestoreRecord(_directory, "Fake", record with { State = "CutoverStarted" }));
		MaintenanceJournal.BeforePublishForTest = null;
		Assert.Equal("Ready", MaintenanceJournal.ReadRestoreRecord(_directory, "Fake", operation)!.State);
		await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
		Assert.True(Databases.CompleteRestore("Fake", operation).Success);
		Assert.Equal(["target-A"], Names());
	}

	[Fact]
	public async Task RollbackRestoresTheCommittedWalWithItsOriginalMain() {
		var backupId = await SeedAsync();
		CreateClosedHotWal();
		var operation = await InterruptAsync(backupId, "SidecarsMoved");
		Assert.False(File.Exists(Path.Combine(_directory, "Fake.db-wal")));
		Assert.True(Databases.RollbackRestore("Fake", operation).Success);
		Assert.Equal(["target-A", "live-B", "wal-only"], Names());
	}

	[Fact]
	public async Task IncompleteQuarantineCannotBeUsedForRollback() {
		var backupId = await SeedAsync();
		CreateClosedHotWal();
		var operation = await InterruptAsync(backupId, "SidecarsMoved");
		var quarantine = Path.Combine(MaintenanceJournal.OperationDirectory(_directory, "Fake", operation), MaintenanceJournal.QuarantineDirectoryName);
		File.Delete(Path.Combine(quarantine, "Fake.db-wal"));
		var before = DatabaseBackupStore.HashFile(Path.Combine(_directory, "Fake.db"));
		Assert.False(Databases.CompleteRestore("Fake", operation).Success);
		Assert.False(Databases.RollbackRestore("Fake", operation).Success);
		Assert.Equal(before, DatabaseBackupStore.HashFile(Path.Combine(_directory, "Fake.db")));
		Assert.NotEmpty(MaintenanceJournal.ListUnfinishedRestores(_directory, "Fake"));
	}

	private void CreateClosedHotWal() {
		var path = Path.Combine(_directory, "Fake.db");
		var files = new Dictionary<string, byte[]>();
		using (var connection = new SqliteConnection(SqliteConnectionPolicy.ForExistingDatabase(path))) {
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; INSERT INTO Items (Name) VALUES ('wal-only')";
			command.ExecuteNonQuery();
			foreach (var suffix in DatabaseRestoreEngine.FileSuffixes) {
				if (!File.Exists(path + suffix)) continue;
				using var input = new FileStream(path + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				using var bytes = new MemoryStream();
				input.CopyTo(bytes);
				files.Add(suffix, bytes.ToArray());
			}
			Assert.Contains("-wal", files.Keys);
		}
		foreach (var file in files) File.WriteAllBytes(path + file.Key, file.Value);
	}

	[Fact]
	public async Task MetadataListingDoesNotReadLockedSnapshotContents() {
		await SeedAsync();
		var backup = Assert.Single(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory));
		using var locked = new FileStream(backup.SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.None);
		Assert.Single(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory));
		Assert.False(DatabaseBackupStore.TryVerify(_directory, "Fake", backup.Category, backup.BackupId, out _, out _));
	}

	[Fact]
	public async Task OrdinaryRestartDoesNotCreateAnExtraRollingSnapshot() {
		await Databases.PrepareAsync<FakeDbContext>();
		Databases.SetDataDirectoryForTest(_directory);
		Databases.Init();
		await Databases.PrepareAsync<FakeDbContext>();
		Assert.Empty(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory));
	}

	[Fact]
	public async Task StatusSelectsLatestAcrossBothSnapshotCategories() {
		await SeedAsync();
		Assert.True(DatabaseBackupStore.TryCreate(_directory, "Fake", DatabaseBackupStore.PreMigrationCategory,
			Path.Combine(_directory, "Fake.db"), [], out var latest, out var failure), failure);
		Assert.Equal(latest, Databases.GetStatus("Fake").LastCompletedBackup!.BackupId);
	}

	[Fact]
	public async Task LegacyFlatBackupCanBeListedAndExplicitlyImported() {
		await SeedAsync();
		var source = Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory).Single().SnapshotPath;
		var legacy = Path.Combine(_directory, "backup", "Fake-20261014.db");
		File.Copy(source, legacy);
		Assert.Contains("Fake-20261014.db", Databases.ListLegacyBackups("Fake"));
		Assert.True(Databases.TryImportLegacyBackup("Fake", legacy, out _, out var failure), failure);
		Assert.True(File.Exists(legacy));
	}

	[Fact]
	public async Task PendingRestoreCannotBeOpenedByBackupCheckpointVerifyOrUnlock() {
		var operation = await InterruptAsync(await SeedAsync(), "SidecarsMoved");
		var path = Path.Combine(_directory, "Fake.db");
		var before = DatabaseBackupStore.HashFile(path);
		Assert.False((await Databases.BackupNowAsync()).Success);
		Assert.False((await Databases.ShutdownAsync()).Success);
		Assert.False(Databases.Verify("Fake", true).Success);
		Assert.False(Databases.Unlock("Fake").Success);
		Assert.Equal(before, DatabaseBackupStore.HashFile(path));
		Assert.True(Databases.CompleteRestore("Fake", operation).Success);
		Assert.Equal(["target-A"], Names());
	}

	[Fact]
	public async Task CaseOnlyContextCollisionIsRejectedBeforeOpeningTheExistingFile() {
		Databases.Configure(typeof(CaseDbContext), new DbContextOptionsBuilder<CaseDbContext>());
		var path = Path.Combine(_directory, "Case.db");
		using (var connection = new SqliteConnection(SqliteConnectionPolicy.ForCreatingDatabase(path))) {
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = "CREATE TABLE Marker (Value TEXT); INSERT INTO Marker VALUES ('preserved')";
			command.ExecuteNonQuery();
		}
		var before = DatabaseBackupStore.HashFile(path);
		await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<caseDbContext>());
		Assert.Equal(before, DatabaseBackupStore.HashFile(path));
	}

	public sealed class CaseDbContext : DbContext;
	public sealed class caseDbContext : DbContext;
}
