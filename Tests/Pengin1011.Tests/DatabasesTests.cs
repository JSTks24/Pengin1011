using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class DatabasesTests {
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
	public async Task Open_CreatesModuleDbUnderData() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			using var db = Databases.Open<FakeDbContext>();
			Assert.True(File.Exists(Path.Combine(DataDir(baseDir), "Fake.db")));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task DataPersists_AcrossReopen() {
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
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task DifferentContexts_GetSeparateFiles() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			await Databases.PrepareAsync<OtherDbContext>();
			await using (var db = Databases.Open<FakeDbContext>()) {
				db.Items.Add(new FakeItem { Name = "Fake库数据" });
				await db.SaveChangesAsync();
			}
			await using (var other = Databases.Open<OtherDbContext>()) {
				other.Things.Add(new OtherItem());
				await other.SaveChangesAsync();
			}
			Assert.True(File.Exists(Path.Combine(DataDir(baseDir), "Fake.db")));
			Assert.True(File.Exists(Path.Combine(DataDir(baseDir), "Other.db")));
			await using var check = Databases.Open<FakeDbContext>();
			Assert.Equal("Fake库数据", (await check.Items.SingleAsync()).Name);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task ConflictingModuleName_Throws() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			await Databases.PrepareAsync<ClashDbContext>();
			await using var first = Databases.Open<ClashDbContext>();

			await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<ClashContext>());
			Assert.Throws<InvalidOperationException>(() => Databases.Open<ClashContext>());
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task GetStatus_ReportsMigrationSizeAndBackup() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			await using (var db = Databases.Open<FakeDbContext>()) {
				db.Items.Add(new FakeItem { Name = "状态检查" });
				await db.SaveChangesAsync();
			}
			await Databases.BackupNowAsync();
			var status = Databases.GetStatus().Single(entry => entry.Database == "Fake");
			Assert.True(status.SizeBytes > 0);
			Assert.NotNull(status.CurrentHistory);
			Assert.NotNull(status.LastCompletedBackup);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task Init_RegistersOrphanDbForBackup() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(DataDir(baseDir));
		try {
			var dataDir = DataDir(baseDir);
			Directory.CreateDirectory(dataDir);
			var legacyPath = Path.Combine(dataDir, "Legacy.db");
			await using (var seed = new RawSqlContext(legacyPath)) {
				await seed.Database.ExecuteSqlRawAsync("CREATE TABLE \"Legacy\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_Legacy\" PRIMARY KEY AUTOINCREMENT)");
				await seed.Database.ExecuteSqlRawAsync("INSERT INTO \"Legacy\" (\"Id\") VALUES (1)");
			}
			Databases.Init();
			await Databases.BackupNowAsync();
			var backups = Databases.ListBackups("Legacy", DatabaseBackupStore.RollingCategory);
			Assert.Single(backups);
			Assert.True(Directory.Exists(Path.Combine(dataDir, "backup", "Legacy")));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}
}
