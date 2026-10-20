using Microsoft.Data.Sqlite;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class DatabaseImportValidationTests : IDisposable {
	private const string Migration = "20261014133521_Init";
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"pengin_import_{Guid.NewGuid():N}");
	private readonly string _dataDirectory;

	public DatabaseImportValidationTests() {
		_dataDirectory = Path.Combine(_directory, "data");
		Directory.CreateDirectory(_dataDirectory);
		HostMaintenance.ResetForTest();
		Databases.SetDataDirectoryForTest(_dataDirectory);
		Databases.Init();
	}

	public void Dispose() {
		HostMaintenance.ResetForTest();
		Databases.SetDataDirectoryForTest(null);
		SqliteConnection.ClearAllPools();
		Directory.Delete(_directory, true);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Import_BusinessTablesWithoutMigrationHistory_IsRejectedAndSourceSurvives(bool historyTable) {
		var source = CreateDatabase("legacy.db", businessTable: true, historyTable: historyTable);
		var hash = DatabaseBackupStore.HashFile(source);

		Assert.False(Databases.TryImportLegacyBackup("Fake", source, out var imported, out var failure));

		Assert.Null(imported);
		Assert.Contains(L.Prefix("DatabaseUntrackedTables"), failure);
		Assert.Equal(hash, DatabaseBackupStore.HashFile(source));
		Assert.Equal("preserved", ReadName(source));
		Assert.Empty(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory));
	}

	[Theory]
	[InlineData(false, false, false)]
	[InlineData(false, true, false)]
	[InlineData(true, true, true)]
	public void Import_EmptyDatabaseOrTrackedBusinessTables_IsAccepted(bool businessTable, bool historyTable, bool appliedHistory) {
		var source = CreateDatabase("legacy.db", businessTable, historyTable, appliedHistory);
		var hash = DatabaseBackupStore.HashFile(source);

		Assert.True(Databases.TryImportLegacyBackup("Fake", source, out var imported, out var failure), failure);

		Assert.Equal(hash, DatabaseBackupStore.HashFile(source));
		var backup = Assert.Single(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory));
		Assert.Equal(imported, backup.BackupId);
		Assert.True(DatabaseBackupStore.TryVerify(_dataDirectory, "Fake", backup.Category, backup.BackupId, out _, out failure), failure);
		Assert.Equal(appliedHistory ? [Migration] : Array.Empty<string>(), backup.History);
		if (businessTable) Assert.Equal("preserved", ReadName(backup.SnapshotPath));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Restore_OldUntrackedSnapshot_LeavesLiveDatabaseAndMigrationIntentUnchanged(bool historyTable) {
		var source = CreateDatabase("legacy.db", businessTable: true, historyTable: historyTable);
		var live = CreateDatabase(Path.Combine("data", "Fake.db"), businessTable: true, historyTable: true, appliedHistory: true);
		var backup = CreateOldSnapshot(source);
		MaintenanceJournal.WriteMigrationIntent(_dataDirectory, "Fake", new MaintenanceJournal.MigrationIntent {
			Database = "Fake",
			OperationId = "existing-migration",
			State = nameof(MaintenanceOperationState.Migrating),
			PreviousHistory = [Migration],
			TargetHistory = [Migration, "20261015033512_AddNameIndex"],
			UpdatedUtc = DateTimeOffset.UtcNow,
		});
		var intentPath = MaintenanceJournal.MigrationIntentPath(_dataDirectory, "Fake");
		var intent = File.ReadAllText(intentPath);
		var liveHash = DatabaseBackupStore.HashFile(live);
		var sourceHash = DatabaseBackupStore.HashFile(source);

		var result = await Databases.RestoreAsync("Fake", backup.BackupId);

		Assert.Equal(RestoreOutcome.Rejected, result.Outcome);
		Assert.Null(result.OperationId);
		Assert.Contains(L.Prefix("DatabaseUntrackedTables"), result.Reason);
		Assert.Equal(liveHash, DatabaseBackupStore.HashFile(live));
		Assert.Equal(sourceHash, DatabaseBackupStore.HashFile(source));
		Assert.Equal(intent, File.ReadAllText(intentPath));
		Assert.Empty(MaintenanceJournal.ListUnfinishedRestores(_dataDirectory, "Fake"));
		Assert.Equal([intentPath], Directory.GetFiles(MaintenanceJournal.OperationRoot(_dataDirectory, "Fake"), "*", SearchOption.AllDirectories));
	}

	[Fact]
	public void Complete_OldReadyRestoreWithUntrackedSnapshot_LeavesOriginalSiteAndJournalUnchanged() {
		var source = CreateDatabase("legacy.db", businessTable: true);
		var live = CreateDatabase(Path.Combine("data", "Fake.db"), businessTable: true, historyTable: true, appliedHistory: true);
		var backup = CreateOldSnapshot(source);
		const string operation = "old-ready-restore";
		var operationDirectory = MaintenanceJournal.OperationDirectory(_dataDirectory, "Fake", operation);
		var quarantine = Path.Combine(operationDirectory, MaintenanceJournal.QuarantineDirectoryName);
		Directory.CreateDirectory(quarantine);
		File.Copy(live, Path.Combine(quarantine, "Fake.db"));
		var originalHash = DatabaseBackupStore.HashFile(live);
		MaintenanceJournal.WriteRestoreRecord(_dataDirectory, "Fake", new MaintenanceJournal.RestoreRecord {
			FormatVersion = 2,
			OperationId = operation,
			Database = "Fake",
			SourceBackupId = backup.BackupId,
			SourceCategory = backup.Category,
			TargetSha256 = DatabaseBackupStore.HashFile(backup.SnapshotPath),
			TargetHistory = [],
			OriginalFiles = DatabaseRestoreEngine.FileSuffixes.Select(suffix => suffix.Length == 0
				? new MaintenanceJournal.OriginalFile(suffix, true, new FileInfo(live).Length, originalHash)
				: new MaintenanceJournal.OriginalFile(suffix, false, 0, null)).ToArray(),
			State = "Ready",
			StartedUtc = DateTimeOffset.UtcNow,
		});
		var journalPath = Path.Combine(operationDirectory, MaintenanceJournal.RecordFileName);
		var journal = File.ReadAllText(journalPath);

		var result = Databases.CompleteRestore("Fake", operation);

		Assert.False(result.Success);
		Assert.Contains(L.Prefix("DatabaseUntrackedTables"), result.Reason);
		Assert.Equal(originalHash, DatabaseBackupStore.HashFile(live));
		Assert.Equal(originalHash, DatabaseBackupStore.HashFile(Path.Combine(quarantine, "Fake.db")));
		Assert.Equal(journal, File.ReadAllText(journalPath));
		Assert.False(File.Exists(Path.Combine(operationDirectory, MaintenanceJournal.StagingFileName)));
		Assert.Equal("preserved", ReadName(source));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Import_WaitsForExistingBackupGateAndPreservesSource(bool appliedHistory) {
		var source = CreateDatabase("legacy.db", businessTable: true, historyTable: true, appliedHistory: appliedHistory);
		var originalHash = DatabaseBackupStore.HashFile(source);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Task<(bool Success, string? Imported, string? Failure)>? import = null;
		await Databases.BackupGate.WaitAsync();
		try {
			import = Task.Run(() => {
				started.SetResult();
				var success = Databases.TryImportLegacyBackup("Fake", source, out var imported, out var failure);
				return (success, imported, failure);
			});
			await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.NotSame(import, await Task.WhenAny(import, Task.Delay(150)));
			Assert.Empty(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory));
			Assert.Equal(originalHash, DatabaseBackupStore.HashFile(source));
		} finally {
			Databases.BackupGate.Release();
			if (import != null) await import.WaitAsync(TimeSpan.FromSeconds(5));
		}

		var result = await import!;
		Assert.Equal(appliedHistory, result.Success);
		if (appliedHistory) Assert.NotNull(result.Imported);
		else Assert.Contains(L.Prefix("DatabaseUntrackedTables"), result.Failure);
		Assert.Equal(originalHash, DatabaseBackupStore.HashFile(source));
		Assert.Equal("preserved", ReadName(source));
	}

	[Fact]
	public async Task Import_QueuedBeforeHandoff_RejectsAfterDataAccessIsPaused() {
		var source = CreateDatabase("legacy.db", businessTable: true, historyTable: true, appliedHistory: true);
		var originalHash = DatabaseBackupStore.HashFile(source);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Task<(bool Success, string? Imported, string? Failure)>? import = null;
		DataDirectoryOwnership? replacementOwner = null;
		await Databases.BackupGate.WaitAsync();
		try {
			import = Task.Run(() => {
				started.SetResult();
				var success = Databases.TryImportLegacyBackup("Fake", source, out var imported, out var failure);
				return (success, imported, failure);
			});
			await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.NotSame(import, await Task.WhenAny(import, Task.Delay(150)));
			Databases.ReleaseOwnership();
			Assert.True(DataDirectoryOwnership.TryAcquire(_dataDirectory, out replacementOwner, out var failure), failure);
			HostMaintenance.DataAccessPaused = true;
		} finally {
			Databases.BackupGate.Release();
			try {
				if (import != null) await import.WaitAsync(TimeSpan.FromSeconds(5));
			} finally {
				replacementOwner?.Dispose();
				HostMaintenance.DataAccessPaused = false;
			}
		}

		var result = await import!;
		Assert.False(result.Success);
		Assert.Null(result.Imported);
		Assert.Equal(L.Get("MaintenanceBusy"), result.Failure);
		Assert.Equal(originalHash, DatabaseBackupStore.HashFile(source));
		Assert.Empty(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory));
	}

	private string CreateDatabase(string file, bool businessTable = false, bool historyTable = false, bool appliedHistory = false) {
		var path = Path.Combine(_directory, file);
		using var connection = new SqliteConnection(SqliteConnectionPolicy.ForCreatingDatabase(path));
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA user_version=1";
		command.ExecuteNonQuery();
		if (businessTable) {
			command.CommandText = "CREATE TABLE Items (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Note TEXT); INSERT INTO Items (Id, Name) VALUES (1, 'preserved')";
			command.ExecuteNonQuery();
		}
		if (historyTable) {
			command.CommandText = "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL)";
			command.ExecuteNonQuery();
		}
		if (appliedHistory) {
			command.CommandText = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ($migration, '10.0.12')";
			command.Parameters.AddWithValue("$migration", Migration);
			command.ExecuteNonQuery();
		}
		return path;
	}

	private DatabaseBackupInfo CreateOldSnapshot(string source) {
		Assert.True(DatabaseBackupStore.TryCreate(_dataDirectory, "Fake", DatabaseBackupStore.RollingCategory, source, [], out var id, out var failure), failure);
		Assert.True(DatabaseBackupStore.TryVerify(_dataDirectory, "Fake", DatabaseBackupStore.RollingCategory, id!, out var backup, out failure), failure);
		Assert.NotNull(backup);
		Assert.Empty(backup.History);
		return backup;
	}

	private static string ReadName(string path) {
		using var connection = SqliteConnectionPolicy.OpenReadOnly(path);
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT Name FROM Items";
		return (string)command.ExecuteScalar()!;
	}
}
