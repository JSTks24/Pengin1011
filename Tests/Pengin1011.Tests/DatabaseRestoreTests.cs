using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class DatabaseRestoreTests {
	private static string TempBase() {
		return Path.Combine(Path.GetTempPath(), $"qq1011_restore_{Guid.NewGuid():N}");
	}

	private static string DataDir(string baseDir) {
		return Path.Combine(baseDir, "data");
	}

	private static void Cleanup(string baseDir) {
		SqliteConnection.ClearAllPools();
		try {
			Directory.Delete(baseDir, true);
		} catch (IOException) {
			SqliteConnection.ClearAllPools();
			Directory.Delete(baseDir, true);
		}
	}

	private static async Task<(string BackupId, string DataDirectory)> SeedWithBackupAsync(string baseDir) {
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		Databases.Init();
		await Databases.PrepareAsync<FakeDbContext>();
		await using (var db = Databases.Open<FakeDbContext>()) {
			db.Items.Add(new FakeItem { Name = "原始行" });
			await db.SaveChangesAsync();
		}
		await Databases.BackupNowAsync();
		var backup = Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory).Single();
		return (backup.BackupId, DataDir(baseDir));
	}

	[Fact]
	public async Task D23_WrongBackupId_IsRejectedAndLeavesTheDatabaseUntouched() {
		var baseDir = TempBase();
		try {
			var (_, dataDirectory) = await SeedWithBackupAsync(baseDir);
			var hashBefore = DatabaseBackupStore.HashFile(Path.Combine(dataDirectory, "Fake.db"));

			var result = await Databases.RestoreAsync("Fake", "absent-backup-id");

			Assert.Equal(RestoreOutcome.Rejected, result.Outcome);
			Assert.Equal(hashBefore, DatabaseBackupStore.HashFile(Path.Combine(dataDirectory, "Fake.db")));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Theory]
	[InlineData("../escape")]
	[InlineData("..\\escape")]
	[InlineData("a/b")]
	public async Task D23_PathTraversalBackupId_IsRejected(string backupId) {
		var baseDir = TempBase();
		try {
			await SeedWithBackupAsync(baseDir);

			var result = await Databases.RestoreAsync("Fake", backupId);

			Assert.Equal(RestoreOutcome.Rejected, result.Outcome);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D23_WrongDatabase_IsRejected() {
		var baseDir = TempBase();
		try {
			var (backupId, _) = await SeedWithBackupAsync(baseDir);

			var result = await Databases.RestoreAsync("Other", backupId);

			Assert.Equal(RestoreOutcome.Rejected, result.Outcome);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D25_Restore_SucceedsAndVerifiesWithoutMixingSidecars() {
		var baseDir = TempBase();
		try {
			var (backupId, dataDirectory) = await SeedWithBackupAsync(baseDir);
			var path = Path.Combine(dataDirectory, "Fake.db");
			await using (var db = Databases.Open<FakeDbContext>()) {
				db.Items.Add(new FakeItem { Name = "快照之后" });
				await db.SaveChangesAsync();
			}

			var result = await Databases.RestoreAsync("Fake", backupId);

			Assert.Equal(RestoreOutcome.Completed, result.Outcome);
			Assert.NotNull(result.OperationId);

			await using var check = new RawSqlContext(path);
			var names = await check.Database.SqlQueryRaw<string>("SELECT \"Name\" AS \"Value\" FROM \"Items\"").ToListAsync();
			Assert.Equal(["原始行"], names);

			Assert.False(File.Exists(path + "-wal"), "恢复主库不得混配旧热 WAL");
			Assert.False(File.Exists(path + "-journal"));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D25_OriginalSiteIsPreservedInQuarantine() {
		var baseDir = TempBase();
		try {
			var (backupId, dataDirectory) = await SeedWithBackupAsync(baseDir);
			var path = Path.Combine(dataDirectory, "Fake.db");
			await using (var db = Databases.Open<FakeDbContext>()) {
				db.Items.Add(new FakeItem { Name = "被覆盖前" });
				await db.SaveChangesAsync();
			}
			var originalHash = DatabaseBackupStore.HashFile(path);

			var result = await Databases.RestoreAsync("Fake", backupId);

			Assert.Equal(RestoreOutcome.Completed, result.Outcome);
			var quarantine = Path.Combine(MaintenanceJournal.OperationDirectory(dataDirectory, "Fake", result.OperationId!), MaintenanceJournal.QuarantineDirectoryName);
			var preserved = Path.Combine(quarantine, "Fake.db");
			Assert.True(File.Exists(preserved), "原现场必须保留");
			Assert.Equal(originalHash, DatabaseBackupStore.HashFile(preserved));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D26_CorruptedSnapshot_IsRejectedAndTheLiveDatabaseSurvives() {
		var baseDir = TempBase();
		try {
			var (backupId, dataDirectory) = await SeedWithBackupAsync(baseDir);
			var path = Path.Combine(dataDirectory, "Fake.db");
			var backup = Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory).Single(info => info.BackupId == backupId);
			var hashBefore = DatabaseBackupStore.HashFile(path);

			await File.WriteAllTextAsync(backup.SnapshotPath, "损坏的快照");

			var result = await Databases.RestoreAsync("Fake", backupId);

			Assert.Equal(RestoreOutcome.Rejected, result.Outcome);
			Assert.Equal(hashBefore, DatabaseBackupStore.HashFile(path));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D27_InterruptedAtCutoverStarted_BlocksNormalOpenAndCanBeRolledBack() {
		var baseDir = TempBase();
		try {
			var (backupId, dataDirectory) = await SeedWithBackupAsync(baseDir);
			var path = Path.Combine(dataDirectory, "Fake.db");

			DatabaseRestoreEngine.BoundaryForTest = stage => {
				if (stage == "CutoverStarted") throw new IOException("injected interruption");
			};
			var result = await Databases.RestoreAsync("Fake", backupId);
			DatabaseRestoreEngine.BoundaryForTest = null;
			Assert.Equal(RestoreOutcome.Failed, result.Outcome);

			var operationId = result.OperationId!;

			await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());

			Assert.Equal(DatabaseState.MaintenanceRequired, Databases.StateOf<FakeDbContext>());

			var rollback = Databases.RollbackRestore("Fake", operationId);
			Assert.True(rollback.Success, rollback.Reason);

			await Databases.PrepareAsync<FakeDbContext>();
			Assert.Equal(DatabaseState.Prepared, Databases.StateOf<FakeDbContext>());
		} finally {
			DatabaseRestoreEngine.BoundaryForTest = null;
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D28_RepeatedRollback_IsIdempotent() {
		var baseDir = TempBase();
		try {
			var (backupId, dataDirectory) = await SeedWithBackupAsync(baseDir);
			DatabaseRestoreEngine.BoundaryForTest = stage => {
				if (stage == "CutoverStarted") throw new IOException("injected interruption");
			};
			var result = await Databases.RestoreAsync("Fake", backupId);
			DatabaseRestoreEngine.BoundaryForTest = null;
			var operationId = result.OperationId!;

			var first = Databases.RollbackRestore("Fake", operationId);
			var second = Databases.RollbackRestore("Fake", operationId);

			Assert.True(first.Success);
			Assert.True(second.Success);
		} finally {
			DatabaseRestoreEngine.BoundaryForTest = null;
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D28_RollbackAfterCompletion_IsClearlyRejected() {
		var baseDir = TempBase();
		try {
			var (backupId, _) = await SeedWithBackupAsync(baseDir);
			var result = await Databases.RestoreAsync("Fake", backupId);

			var rollback = Databases.RollbackRestore("Fake", result.OperationId!);

			Assert.False(rollback.Success);
			Assert.Contains(L.Prefix("RestoreAlreadyCompleted"), rollback.Reason);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D29_Unlock_DoesNotClaimMigrationSucceededAndKeepsTheIntent() {
		var baseDir = TempBase();
		try {
			Databases.SetDataDirectoryForTest(DataDir(baseDir));
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			var dataDirectory = DataDir(baseDir);
			MaintenanceJournal.WriteMigrationIntent(dataDirectory, "Fake", new MaintenanceJournal.MigrationIntent {
				Database = "Fake",
				OperationId = "op-interrupted",
				State = nameof(MaintenanceOperationState.Migrating),
				PreviousHistory = [],
				TargetHistory = ["20260919000001_Init"],
				UpdatedUtc = DateTimeOffset.UtcNow,
			});

			var unlock = Databases.Unlock("Fake");

			Assert.False(unlock.Success);
			Assert.Contains(L.Prefix("UnlockWithPendingIntent"), unlock.Reason);
			Assert.NotNull(MaintenanceJournal.ReadMigrationIntent(dataDirectory, "Fake"));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D29_Verify_RequiresExplicitConfirmation() {
		var baseDir = TempBase();
		try {
			await SeedWithBackupAsync(baseDir);

			Assert.False(Databases.Verify("Fake", acceptInterrupted: false).Success);
			Assert.True(Databases.Verify("Fake", acceptInterrupted: true).Success);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D30_LegacyBackup_ImportKeepsSourceAndValidatesContent() {
		var baseDir = TempBase();
		try {
			Databases.SetDataDirectoryForTest(DataDir(baseDir));
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			var legacy = Path.Combine(baseDir, "legacy-flat.db");
			await using (var seed = new RawSqlContext(legacy)) {
				await seed.Database.ExecuteSqlRawAsync("CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)");
			}

			Assert.True(Databases.TryImportLegacyBackup("Fake", legacy, out var imported, out var failure), failure);
			Assert.NotNull(imported);
			Assert.True(File.Exists(legacy), "源文件必须保留");

			Assert.Contains(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory), info => info.BackupId == imported);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D30_LegacyBackup_ThatIsNotSqlite_IsRejected() {
		var baseDir = TempBase();
		try {
			Databases.SetDataDirectoryForTest(DataDir(baseDir));
			Databases.Init();
			var bogus = Path.Combine(baseDir, "bogus.db");
			await File.WriteAllTextAsync(bogus, "这不是 SQLite");

			Assert.False(Databases.TryImportLegacyBackup("Fake", bogus, out _, out var failure));
			Assert.NotNull(failure);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D32_Status_ReportsMissingDatabaseWithoutCreatingIt() {
		var baseDir = TempBase();
		try {
			Databases.SetDataDirectoryForTest(DataDir(baseDir));
			Databases.Init();

			var status = Databases.GetStatus("Fake");

			Assert.Equal(DatabaseState.NotPresent, status.State);
			Assert.False(File.Exists(Path.Combine(DataDir(baseDir), "Fake.db")));
			Assert.Contains("missing", status.Notes);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D32_Status_ReportsEmptyHistoryAsAnExplicitState() {
		var baseDir = TempBase();
		try {
			Databases.SetDataDirectoryForTest(DataDir(baseDir));
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();

			var status = Databases.GetStatus("Fake");

			Assert.Equal(DatabaseState.Prepared, status.State);
			Assert.NotNull(status.CurrentHistory);
			Assert.True(status.SizeBytes > 0);
			Assert.Null(status.PendingMaintenance);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D33_RealConnectionReportsVersionAndPragmas() {
		var baseDir = TempBase();
		try {
			Databases.SetDataDirectoryForTest(DataDir(baseDir));
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			var path = Path.Combine(DataDir(baseDir), "Fake.db");

			using var connection = SqliteConnectionPolicy.OpenExisting(path);

			Assert.Equal("wal", SqliteConnectionPolicy.ReadJournalMode(connection), ignoreCase: true);
			Assert.Equal(2L, SqliteConnectionPolicy.ReadSynchronous(connection));
			Assert.Equal(1L, SqliteConnectionPolicy.ReadForeignKeys(connection));
			Assert.Equal(1L, SqliteConnectionPolicy.ReadIntegrityCheck(connection));
			Assert.Equal(0L, SqliteConnectionPolicy.ReadForeignKeyCheck(connection));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D17_BusinessConnectionsCarryThePolicyPragmas() {
		var baseDir = TempBase();
		try {
			Databases.SetDataDirectoryForTest(DataDir(baseDir));
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();

			await using var db = Databases.Open<FakeDbContext>();
			var connection = (SqliteConnection)db.Database.GetDbConnection();
			await connection.OpenAsync();

			Assert.Equal(1L, SqliteConnectionPolicy.ReadForeignKeys(connection));
			Assert.Equal(2L, SqliteConnectionPolicy.ReadSynchronous(connection));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}
}
