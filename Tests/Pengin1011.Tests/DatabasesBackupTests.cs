using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class DatabasesBackupTests {
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
	public async Task D09_RollingKeepsFiveCompletedSnapshotsAndDropsNothingElse() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			await using (var db = Databases.Open<FakeDbContext>()) {
				db.Items.Add(new FakeItem { Name = "备份我" });
				await db.SaveChangesAsync();
			}
			for (var i = 0; i < 7; i++) {
				await Databases.BackupNowAsync();
			}

			var rolling = Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory);
			Assert.Equal(DatabaseBackupStore.RollingKeepCount, rolling.Count);
			Assert.All(rolling, info => Assert.True(DatabaseBackupStore.TryVerify(DataDir(baseDir), "Fake", info.Category, info.BackupId, out _, out _)));
			Assert.All(rolling, info => Assert.True(File.Exists(info.SnapshotPath)));

			await using var snapshot = new RawSqlContext(rolling[^1].SnapshotPath);
			var count = await snapshot.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"Items\"").SingleAsync();
			Assert.Equal(1L, count);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D10_MetadataAndDigestAreRecordedForEveryCompletedSnapshot() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			await using (var db = Databases.Open<FakeDbContext>()) {
				db.Items.Add(new FakeItem { Name = "摘要" });
				await db.SaveChangesAsync();
			}

			await Databases.BackupNowAsync();

			var info = Assert.Single(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory));
			Assert.True(info.SizeBytes > 0);
			Assert.NotEmpty(info.History);
			Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(info.SnapshotPath)!, DatabaseBackupStore.MetadataFileName)));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D10_ListIsMetadataOnly_AndVerifyRejectsHashMismatch() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			await using (var db = Databases.Open<FakeDbContext>()) {
				db.Items.Add(new FakeItem { Name = "篡改" });
				await db.SaveChangesAsync();
			}
			await Databases.BackupNowAsync();
			var info = Assert.Single(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory));

			await using (var tamper = new RawSqlContext(info.SnapshotPath)) {
				await tamper.Database.ExecuteSqlRawAsync("INSERT INTO \"Items\" (\"Name\") VALUES ('tampered')");
			}
			SqliteConnection.ClearAllPools();

			Assert.Single(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory));
			Assert.False(DatabaseBackupStore.TryVerify(DataDir(baseDir), "Fake", info.Category, info.BackupId, out _, out _));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D10_PartialDirectory_IsNeverListedAsCompleted() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			var categoryRoot = DatabaseBackupStore.CategoryRoot(DataDir(baseDir), "Fake", DatabaseBackupStore.RollingCategory);
			Directory.CreateDirectory(Path.Combine(categoryRoot, "interrupted" + DatabaseBackupStore.PartialSuffix));

			Assert.Empty(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D08_PreMigrationSnapshot_LivesInItsOwnCategory() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			await using (var db = Databases.Open<FakeDbContext>()) {
				db.Items.Add(new FakeItem { Name = "升级前" });
				await db.SaveChangesAsync();
			}
			var path = Path.Combine(DataDir(baseDir), "Fake.db");

			Assert.True(DatabaseBackupStore.TryCreate(DataDir(baseDir), "Fake", DatabaseBackupStore.PreMigrationCategory, path, ["20260919000002_AddNote"], out var backupId, out var failure), failure);

			var preMigration = Databases.ListBackups("Fake", DatabaseBackupStore.PreMigrationCategory);
			Assert.Single(preMigration);
			Assert.Equal(backupId, preMigration[0].BackupId);

			await Databases.BackupNowAsync();
			Assert.Single(Databases.ListBackups("Fake", DatabaseBackupStore.PreMigrationCategory));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task Shutdown_CheckpointsAndBackups() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			await using (var db = Databases.Open<FakeDbContext>()) {
				db.Items.Add(new FakeItem { Name = "关停保留" });
				await db.SaveChangesAsync();
			}

			var result = await Databases.ShutdownAsync();

			Assert.True(result.Success);
			var rolling = Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory);
			Assert.NotEmpty(rolling);
			await using var check = new RawSqlContext(rolling[^1].SnapshotPath);
			var count = await check.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"Items\"").SingleAsync();
			Assert.Equal(1L, count);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}
}
