using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class DatabaseDriftAndRetentionTests {
	private static string TempBase() {
		return Path.Combine(Path.GetTempPath(), $"qq1011_drift_{Guid.NewGuid():N}");
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
			db.Items.Add(new FakeItem { Name = "行" });
			await db.SaveChangesAsync();
		}
		await Databases.BackupNowAsync();
		var backup = Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory).Single();
		return (backup.BackupId, DataDir(baseDir));
	}

	[Fact]
	public async Task D02_ModelMatchingItsSnapshot_HasNoPendingModelChanges() {
		var options = new DbContextOptionsBuilder<FakeDbContext>().UseSqlite("Data Source=:memory:").Options;
		using var context = new FakeDbContext(options);

		Assert.False(context.Database.HasPendingModelChanges());
	}

	[Fact]
	public async Task D02_ModelDriftWithNoPendingMigration_IsRejected() {
		var baseDir = TempBase();
		try {
			Databases.SetDataDirectoryForTest(DataDir(baseDir));
			Databases.Init();

			// 该 context 的模型始终比自身 ModelSnapshot 多一列，且迁移只有 Init ⇒ 没有 pending migration，
			// 但模型漂移必须被检出并拒绝准备。
			await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<DriftedDbContext>());

			Assert.Equal(DatabaseState.Failed, Databases.StateOf<DriftedDbContext>());
			Assert.False(File.Exists(Path.Combine(DataDir(baseDir), "Drifted.db")));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D11_RollingSnapshotSucceeds_KeepsTheCompletedSetAtTheRetentionLimit() {
		var baseDir = TempBase();
		try {
			await SeedAsync(baseDir);

			for (var i = 0; i < DatabaseBackupStore.RollingKeepCount + 3; i++) {
				await Databases.BackupNowAsync();
			}

			var rolling = Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory);

			Assert.Equal(DatabaseBackupStore.RollingKeepCount, rolling.Count);
			Assert.All(rolling, info => Assert.True(DatabaseBackupStore.TryVerify(DataDir(baseDir), "Fake", info.Category, info.BackupId, out _, out _)));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D11_PruneFailure_DoesNotMarkTheNewSnapshotAsFailed() {
		var baseDir = TempBase();
		try {
			var (_, dataDirectory) = await SeedAsync(baseDir);
			for (var i = 0; i < DatabaseBackupStore.RollingKeepCount; i++) {
				await Databases.BackupNowAsync();
			}
			var categoryRoot = DatabaseBackupStore.CategoryRoot(dataDirectory, "Fake", DatabaseBackupStore.RollingCategory);
			var before = Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory).Count;
			SqliteConnection.ClearAllPools();

			// 独占打开最旧快照，使裁剪端的 Directory.Delete 必然失败；新快照本身仍应成功发布，
			// 并把裁剪失败降级为 retention warning，而不是把整次备份判为失败。
			var oldest = Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory)[0];
			using (new FileStream(oldest.SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.None)) {
				var result = await Databases.BackupNowAsync();

				Assert.Empty(result.Failures);
				Assert.True(result.Success, string.Join("; ", result.Failures.Select(failure => failure.Reason)));
			}

			Assert.True(Directory.Exists(categoryRoot));
			Assert.True(Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory).Count >= before);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}
}
