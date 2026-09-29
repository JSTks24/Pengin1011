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
	public void Open_WithoutMigrations_Throws() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(baseDir);
		try {
			Databases.Init();
			Assert.Throws<InvalidOperationException>(() => Databases.Open<BareDbContext>());
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task Reopen_AppliesPendingMigration_KeepsData() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(baseDir);
		try {
			Databases.Init();
			var dbPath = Path.Combine(DataDir(baseDir), "Fake.db");
			await using (var scaffold = new RawSqlContext(dbPath)) {
				await scaffold.Database.ExecuteSqlRawAsync("CREATE TABLE \"Items\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_Items\" PRIMARY KEY AUTOINCREMENT, \"Name\" TEXT NOT NULL)");
				await scaffold.Database.ExecuteSqlRawAsync("CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)");
				await scaffold.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260919000001_Init', '10.0.12')");
				await scaffold.Database.ExecuteSqlRawAsync("INSERT INTO \"Items\" (\"Name\") VALUES ('旧数据')");
			}
			await using var db = Databases.Open<FakeDbContext>();
			var row = await db.Items.SingleAsync();
			Assert.Equal("旧数据", row.Name);
			Assert.Null(row.Note);
			row.Note = "升级后补写";
			await db.SaveChangesAsync();
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}
}
