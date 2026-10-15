using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class DatabaseInterruptionTests {
	private static string TempBase() {
		return Path.Combine(Path.GetTempPath(), $"qq1011_interrupt_{Guid.NewGuid():N}");
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

	private static async Task<(string BackupId, string DataDirectory)> SeedAsync(string baseDir) {
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		Databases.Init();
		await Databases.PrepareAsync<FakeDbContext>();
		await using (var db = Databases.Open<FakeDbContext>()) {
			db.Items.Add(new FakeItem { Name = "原始行" });
			await db.SaveChangesAsync();
		}
		await Databases.BackupNowAsync();
		var backup = Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory).Single();
		await using (var live = Databases.Open<FakeDbContext>()) {
			live.Items.Add(new FakeItem { Name = "现库行" });
			await live.SaveChangesAsync();
		}
		return (backup.BackupId, DataDir(baseDir));
	}

	private static async Task<string> InterruptRestoreAsync(string dataDirectory, string backupId, string stage) {
		DatabaseRestoreEngine.BoundaryForTest = boundary => {
			if (boundary == stage) throw new IOException("injected file boundary failure");
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

	[Theory]
	[InlineData("CutoverStarted")]
	[InlineData("SidecarsMoved")]
	[InlineData("MainReplaced")]
	public async Task D27_EveryInterruptionStage_BlocksNormalStartup(string stage) {
		var baseDir = TempBase();
		try {
			var (backupId, dataDirectory) = await SeedAsync(baseDir);
			var operationId = await InterruptRestoreAsync(dataDirectory, backupId, stage);

			await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());

			Assert.Equal(DatabaseState.MaintenanceRequired, Databases.StateOf<FakeDbContext>());
			Assert.Throws<InvalidOperationException>(() => Databases.Open<FakeDbContext>());

			var pending = Databases.PendingMaintenance("Fake");
			Assert.Contains(pending, entry => entry.Category == "restore" && entry.OperationId == operationId);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Theory]
	[InlineData("CutoverStarted")]
	[InlineData("SidecarsMoved")]
	[InlineData("MainReplaced")]
	public async Task D27_EachInterruptedStage_IsRecoverableByRollback(string stage) {
		var baseDir = TempBase();
		try {
			var (backupId, dataDirectory) = await SeedAsync(baseDir);
			var path = Path.Combine(dataDirectory, "Fake.db");
			var operationId = await InterruptRestoreAsync(dataDirectory, backupId, stage);

			var rollback = Databases.RollbackRestore("Fake", operationId);
			Assert.True(rollback.Success, rollback.Reason);

			await Databases.PrepareAsync<FakeDbContext>();

			Assert.Equal(DatabaseState.Prepared, Databases.StateOf<FakeDbContext>());
			await using var check = new RawSqlContext(path);
			var names = await check.Database.SqlQueryRaw<string>("SELECT \"Name\" AS \"Value\" FROM \"Items\"").ToListAsync();
			Assert.Equal(["原始行", "现库行"], names);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Theory]
	[InlineData("CutoverStarted")]
	[InlineData("MainReplaced")]
	public async Task D27_EachInterruptedStage_IsRecoverableByCompletion(string stage) {
		var baseDir = TempBase();
		try {
			var (backupId, dataDirectory) = await SeedAsync(baseDir);
			var operationId = await InterruptRestoreAsync(dataDirectory, backupId, stage);

			var completed = Databases.CompleteRestore("Fake", operationId);

			Assert.True(completed.Success, completed.Reason);
			using (var connection = SqliteConnectionPolicy.OpenReadOnly(Path.Combine(dataDirectory, "Fake.db"))) {
				using var query = connection.CreateCommand();
				query.CommandText = "SELECT COUNT(*) FROM Items";
				Assert.Equal(1L, query.ExecuteScalar());
			}
			await Databases.PrepareAsync<FakeDbContext>();
			Assert.Equal(DatabaseState.Prepared, Databases.StateOf<FakeDbContext>());
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D27_RecoverWithUnknownOperation_IsRejected() {
		var baseDir = TempBase();
		try {
			var (backupId, dataDirectory) = await SeedAsync(baseDir);
			var operationId = await InterruptRestoreAsync(dataDirectory, backupId, "CutoverStarted");

			var missing = Databases.CompleteRestore("Fake", "no-such-operation");

			Assert.False(missing.Success);
			Assert.Contains(L.Prefix("RestoreOperationUnknown"), missing.Reason);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D15_InterruptedMigrationIntent_BlocksStartupAndVerifyClearsIt() {
		var baseDir = TempBase();
		try {
			var (_, dataDirectory) = await SeedAsync(baseDir);
			MaintenanceJournal.WriteMigrationIntent(dataDirectory, "Fake", new MaintenanceJournal.MigrationIntent {
				Database = "Fake",
				OperationId = "mig-op",
				State = nameof(MaintenanceOperationState.Migrating),
				PreviousHistory = [],
				TargetHistory = DatabaseBackupStore.ReadHistory(Path.Combine(dataDirectory, "Fake.db")),
				UpdatedUtc = DateTimeOffset.UtcNow,
			});

			await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
			Assert.Equal(DatabaseState.MaintenanceRequired, Databases.StateOf<FakeDbContext>());

			var verified = Databases.Verify("Fake", acceptInterrupted: true);
			Assert.True(verified.Success, verified.Reason);

			await Databases.PrepareAsync<FakeDbContext>();
			Assert.Equal(DatabaseState.Prepared, Databases.StateOf<FakeDbContext>());
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D13_ResidualMigrationLock_FailsFastWithoutDeletingIt() {
		var baseDir = TempBase();
		try {
			Databases.SetDataDirectoryForTest(DataDir(baseDir));
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			var path = Path.Combine(DataDir(baseDir), "Fake.db");
			SqliteConnection.ClearAllPools();
			using (var connection = new SqliteConnection(SqliteConnectionPolicy.ForExistingDatabase(path))) {
				connection.Open();
				using var create = connection.CreateCommand();
				create.CommandText = "CREATE TABLE IF NOT EXISTS \"__EFMigrationsLock\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK___EFMigrationsLock\" PRIMARY KEY, \"Timestamp\" TEXT NOT NULL)";
				create.ExecuteNonQuery();
				using var insert = connection.CreateCommand();
				insert.CommandText = "INSERT OR REPLACE INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, '2026-01-01')";
				insert.ExecuteNonQuery();
			}
			Databases.SetDataDirectoryForTest(DataDir(baseDir));

			await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());

			Assert.True(File.Exists(path));
			using var verify = new SqliteConnection(SqliteConnectionPolicy.ForExistingDatabase(path));
			verify.Open();
			using var count = verify.CreateCommand();
			count.CommandText = "SELECT COUNT(*) FROM __EFMigrationsLock";
			Assert.Equal(1L, Convert.ToInt64(count.ExecuteScalar()));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D29_Unlock_OnlyClearsTheLockStateAndKeepsTheDatabase() {
		var baseDir = TempBase();
		try {
			Databases.SetDataDirectoryForTest(DataDir(baseDir));
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			var path = Path.Combine(DataDir(baseDir), "Fake.db");
			var historyBefore = DatabaseBackupStore.ReadHistory(path);
			SqliteConnection.ClearAllPools();
			using (var connection = new SqliteConnection(SqliteConnectionPolicy.ForExistingDatabase(path))) {
				connection.Open();
				using var create = connection.CreateCommand();
				create.CommandText = "CREATE TABLE IF NOT EXISTS \"__EFMigrationsLock\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK___EFMigrationsLock\" PRIMARY KEY, \"Timestamp\" TEXT NOT NULL)";
				create.ExecuteNonQuery();
				using var insert = connection.CreateCommand();
				insert.CommandText = "INSERT OR REPLACE INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, '2026-01-01')";
				insert.ExecuteNonQuery();
			}
			Databases.SetDataDirectoryForTest(DataDir(baseDir));

			var unlock = Databases.Unlock("Fake");

			Assert.True(unlock.Success, unlock.Reason);
			Assert.Equal(historyBefore, DatabaseBackupStore.ReadHistory(path));

			await Databases.PrepareAsync<FakeDbContext>();
			Assert.Equal(DatabaseState.Prepared, Databases.StateOf<FakeDbContext>());
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D29_Unlock_OnCleanDatabase_ReportsNothingToDo() {
		var baseDir = TempBase();
		try {
			await SeedAsync(baseDir);
			Databases.SetDataDirectoryForTest(DataDir(baseDir));

			var unlock = Databases.Unlock("Fake");

			Assert.True(unlock.Success);
			Assert.Contains(L.Prefix("UnlockNothingToDo"), unlock.Reason);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D19_DeletedPreparedDatabase_IsNotSilentlyRecreated() {
		var baseDir = TempBase();
		try {
			var (_, dataDirectory) = await SeedAsync(baseDir);
			var path = Path.Combine(dataDirectory, "Fake.db");
			SqliteConnection.ClearAllPools();
			File.Delete(path);

			await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());

			Assert.False(File.Exists(path), "已准备库被外部删除后不得静默重建为空库");
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public void D18_DatabaseIdentity_IsStableAndPathFree() {
		Assert.True(DatabaseIdentity.TryDerive(typeof(FakeDbContext), out var id, out _));
		var fileName = DatabaseIdentity.FileName(id);

		Assert.Equal("Fake", id);
		Assert.Equal("Fake.db", fileName);
		Assert.DoesNotContain(Path.DirectorySeparatorChar, fileName);
		Assert.DoesNotContain("..", fileName);
	}

	[Fact]
	public void D18_IdentityIsDistinctPerContext() {
		Assert.True(DatabaseIdentity.TryDerive(typeof(FakeDbContext), out var fake, out _));
		Assert.True(DatabaseIdentity.TryDerive(typeof(OtherDbContext), out var other, out _));

		Assert.NotEqual(fake, other);
		Assert.Equal("Other", other);
	}

	[Fact]
	public async Task D18_ConnectionStringWithSpecialCharacters_IsBuiltNotInterpolated() {
		var baseDir = TempBase();
		try {
			var hostile = Path.Combine(baseDir, "data;Mode=Memory");
			Databases.SetDataDirectoryForTest(hostile);
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();

			var target = Path.Combine(hostile, "Fake.db");
			Assert.True(File.Exists(target), "分号必须作为路径内容处理，而不是连接串分隔符");

			using var connection = SqliteConnectionPolicy.OpenExisting(target);
			Assert.Equal(1L, SqliteConnectionPolicy.ReadIntegrityCheck(connection));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public void D22_ExplicitMissingDirectory_IsNotSilentlyReplacedByTheDefault() {
		var missing = Path.Combine(Path.GetTempPath(), $"qq1011_absent_{Guid.NewGuid():N}", "absent-directory");
		var options = new DatabasePathOptions(missing, null, CreateIfMissing: false);

		Assert.False(Databases.TryResolveDataDirectory(options, out var resolved, out var failure));

		Assert.Equal(missing, failure);
		Assert.NotEqual(DataDirectoryResolver.Default, resolved);
	}

	[Fact]
	public void D22_ExplicitDirectory_IsNotOverriddenByTheDefault() {
		var explicitDirectory = Path.Combine(Path.GetTempPath(), $"qq1011_explicit_{Guid.NewGuid():N}");
		try {
			var options = new DatabasePathOptions(explicitDirectory, null, CreateIfMissing: true);

			Assert.True(Databases.TryResolveDataDirectory(options, out var resolved, out _));

			Assert.Equal(explicitDirectory, resolved);
			Assert.NotEqual(DataDirectoryResolver.Default, resolved);
		} finally {
			try { Directory.Delete(explicitDirectory, true); } catch (IOException) { }
		}
	}

	[Fact]
	public void D22_PathWithSpacesAndSemicolonsAndChinese_IsUsable() {
		var directory = Path.Combine(Path.GetTempPath(), $"qq1011 空格;分号 中文_{Guid.NewGuid():N}");
		try {
			var options = new DatabasePathOptions(directory, null, CreateIfMissing: true);

			Assert.True(Databases.TryResolveDataDirectory(options, out var resolved, out _));

			Assert.Equal(directory, resolved);
		} finally {
			try { Directory.Delete(directory, true); } catch (IOException) { }
		}
	}

	[Fact]
	public void D22_RelativeExplicitPath_ResolvesAgainstTheProgramDirectory() {
		var options = new DatabasePathOptions("relative-data", null, CreateIfMissing: true);
		var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "relative-data"));

		Assert.True(Databases.TryResolveDataDirectory(options, out var resolved, out _));

		Assert.Equal(expected, resolved);
		try { Directory.Delete(expected, true); } catch (IOException) { }
	}
}
