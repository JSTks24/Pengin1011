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
	public async Task Backup_CreatesSnapshotAndPrunes() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(baseDir);
		try {
			Databases.Init();
			await using (var db = Databases.Open<FakeDbContext>()) {
				db.Items.Add(new FakeItem { Name = "备份我" });
				await db.SaveChangesAsync();
			}
			var backupDir = Path.Combine(DataDir(baseDir), "backup");
			for (var i = 0; i < 7; i++) {
				await Databases.BackupNowAsync();
			}
			var backups = Directory.GetFiles(backupDir, "Fake-*.db");
			Assert.Equal(5, backups.Length);
			await using var snapshot = new RawSqlContext(backups[0]);
			var count = await snapshot.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"Items\"").SingleAsync();
			Assert.Equal(1L, count);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task Shutdown_CheckpointsAndBackups() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(baseDir);
		try {
			Databases.Init();
			await using (var db = Databases.Open<FakeDbContext>()) {
				db.Items.Add(new FakeItem { Name = "关停保留" });
				await db.SaveChangesAsync();
			}
			await Databases.ShutdownAsync();
			var backupDir = Path.Combine(DataDir(baseDir), "backup");
			Assert.True(Directory.GetFiles(backupDir, "Fake-*.db").Length >= 1);
			var copyPath = Path.Combine(Path.GetTempPath(), $"qq1011_snap_{Guid.NewGuid():N}.db");
			File.Copy(Path.Combine(DataDir(baseDir), "Fake.db"), copyPath);
			try {
				await using var check = new RawSqlContext(copyPath);
				var count = await check.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"Items\"").SingleAsync();
				Assert.Equal(1L, count);
			} finally {
				SqliteConnection.ClearAllPools();
				File.Delete(copyPath);
			}
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}
}
