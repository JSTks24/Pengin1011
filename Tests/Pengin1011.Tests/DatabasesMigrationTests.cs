using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class DatabasesMigrationTests {
	private static string TempBase() {
		return Path.Combine(Path.GetTempPath(), $"qq1011_ef_{Guid.NewGuid():N}");
	}

	private static string DataDir(string baseDir) {
		return Path.Combine(baseDir, "data");
	}

	private static void Cleanup(string baseDir) {
		try {
			Directory.Delete(baseDir, true);
		} catch (IOException) {
			SqliteConnection.ClearAllPools();
			Directory.Delete(baseDir, true);
		}
	}

	[Fact]
	public async Task D01_ContextWithoutMigrations_IsRejectedByPrepare() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();

			await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<BareDbContext>());
			Assert.Equal(DatabaseState.Failed, Databases.StateOf<BareDbContext>());
			Assert.Throws<InvalidOperationException>(() => Databases.Open<BareDbContext>());
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D03_PreparedDatabase_KeepsRowsAndReachesTheToolGeneratedHistory() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			await using (var db = Databases.Open<FakeDbContext>()) {
				db.Items.Add(new FakeItem { Name = "保留我" });
				await db.SaveChangesAsync();
			}

			await Databases.PrepareAsync<FakeDbContext>();
			await using (var db = Databases.Open<FakeDbContext>()) {
				var row = await db.Items.SingleAsync();
				Assert.Equal("保留我", row.Name);
			}

			var history = Databases.GetStatus("Fake").CurrentHistory;
			Assert.NotNull(history);
			Assert.EndsWith("_AddNameIndex", history);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D05_RepeatedPrepareAfterPrepared_DoesNotMigrateAgain() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			var path = Path.Combine(DataDir(baseDir), "Fake.db");
			var historyBefore = DatabaseBackupStore.ReadHistory(path);
			var lockTableBefore = HasMigrationLockTable(path);

			for (var i = 0; i < 100; i++) {
				await Databases.PrepareAsync<FakeDbContext>();
			}

			Assert.Equal(historyBefore, DatabaseBackupStore.ReadHistory(path));
			Assert.Equal(lockTableBefore, HasMigrationLockTable(path));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D05_PreparedDatabase_OpensIndependentContexts() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();

			await using var first = Databases.Open<FakeDbContext>();
			await using var second = Databases.Open<FakeDbContext>();
			await using var third = Databases.Open<FakeDbContext>();

			Assert.NotSame(first, second);
			Assert.NotSame(second, third);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D16_PrepareFailure_LeavesFailedAndOpenDoesNotRetryMigration() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			var path = Path.Combine(DataDir(baseDir), "Fake.db");
			Directory.CreateDirectory(DataDir(baseDir));
			await File.WriteAllTextAsync(path, "不是 SQLite 文件");

			await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());

			Assert.Equal(DatabaseState.Failed, Databases.StateOf<FakeDbContext>());
			Assert.Throws<InvalidOperationException>(() => Databases.Open<FakeDbContext>());
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D07_DatabaseWithTablesButNoHistory_IsNotTreatedAsFresh() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			var dbPath = Path.Combine(DataDir(baseDir), "Fake.db");
			Directory.CreateDirectory(DataDir(baseDir));
			await using (var scaffold = new RawSqlContext(dbPath)) {
				await scaffold.Database.ExecuteSqlRawAsync("CREATE TABLE \"Items\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_Items\" PRIMARY KEY AUTOINCREMENT, \"Name\" TEXT NOT NULL)");
			}

			await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());

			Assert.Equal(DatabaseState.Failed, Databases.StateOf<FakeDbContext>());
			await using var check = new RawSqlContext(dbPath);
			var names = await check.Database.SqlQueryRaw<string>("SELECT \"Name\" AS \"Value\" FROM \"Items\"").ToListAsync();
			Assert.Empty(names);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D07_UnknownMigrationInHistory_IsRejectedWithoutDown() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			var dbPath = Path.Combine(DataDir(baseDir), "Fake.db");
			Directory.CreateDirectory(DataDir(baseDir));
			await using (var scaffold = new RawSqlContext(dbPath)) {
				await scaffold.Database.ExecuteSqlRawAsync("CREATE TABLE \"Items\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_Items\" PRIMARY KEY AUTOINCREMENT, \"Name\" TEXT NOT NULL, \"Note\" TEXT NULL)");
				await scaffold.Database.ExecuteSqlRawAsync("CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)");
				await scaffold.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('99990101000000_Future', '10.0.12')");
			}

			await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());

			await using var check = new RawSqlContext(dbPath);
			var history = await check.Database.SqlQueryRaw<string>("SELECT \"MigrationId\" AS \"Value\" FROM \"__EFMigrationsHistory\"").ToListAsync();
			Assert.Equal(["99990101000000_Future"], history);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D06_ConcurrentPrepare_WaitsForTheSameSharedTask() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			var tasks = Enumerable.Range(0, 8).Select(_ => Databases.PrepareAsync<FakeDbContext>()).ToArray();
			await Task.WhenAll(tasks);
			Assert.Equal(DatabaseState.Prepared, Databases.StateOf<FakeDbContext>());
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D06_OneWaiterCancelled_DoesNotBreakTheSharedPreparation() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			using var cts = new CancellationTokenSource();
			var cancelled = Databases.PrepareAsync<FakeDbContext>(cts.Token);
			cts.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

			await Databases.PrepareAsync<FakeDbContext>();

			Assert.Equal(DatabaseState.Prepared, Databases.StateOf<FakeDbContext>());
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	private static bool HasMigrationLockTable(string path) {
		using var connection = new SqliteConnection($"Data Source={path}");
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsLock'";
		return Convert.ToInt64(command.ExecuteScalar()) > 0;
	}
}
