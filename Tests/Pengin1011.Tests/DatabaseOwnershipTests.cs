using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class DatabaseOwnershipTests {
	private static string TempBase() {
		return Path.Combine(Path.GetTempPath(), $"qq1011_own_{Guid.NewGuid():N}");
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

	[Fact]
	public void D20_SecondOwnerOfTheSameDirectory_IsRefused() {
		var baseDir = TempBase();
		try {
			Directory.CreateDirectory(baseDir);

			Assert.True(DataDirectoryOwnership.TryAcquire(baseDir, out var first, out var firstFailure), firstFailure);
			Assert.NotNull(first);
			using (first) {
				Assert.False(DataDirectoryOwnership.TryAcquire(baseDir, out var second, out var secondFailure));
				Assert.Null(second);
				Assert.NotNull(secondFailure);
			}
		} finally {
			Cleanup(baseDir);
		}
	}

	[Fact]
	public void D20_OwnerIsReleasedWithTheHandle() {
		var baseDir = TempBase();
		try {
			Directory.CreateDirectory(baseDir);

			Assert.True(DataDirectoryOwnership.TryAcquire(baseDir, out var first, out _));
			first!.Dispose();

			Assert.True(DataDirectoryOwnership.TryAcquire(baseDir, out var second, out var failure), failure);
			second!.Dispose();
		} finally {
			Cleanup(baseDir);
		}
	}

	[Fact]
	public void D20_ResidualLockFile_IsNotTreatedAsOwnershipEvidence() {
		var baseDir = TempBase();
		try {
			Directory.CreateDirectory(baseDir);
			File.WriteAllText(Path.Combine(baseDir, DataDirectoryOwnership.LockFileName), "stale");

			Assert.True(DataDirectoryOwnership.TryAcquire(baseDir, out var ownership, out var failure), failure);
			ownership!.Dispose();
		} finally {
			Cleanup(baseDir);
		}
	}

	[Fact]
	public void D20_AliasedPath_ResolvesToTheSamePhysicalDirectory() {
		var baseDir = TempBase();
		try {
			Directory.CreateDirectory(baseDir);
			var aliased = Path.Combine(baseDir, ".", "sub", "..");

			Assert.Equal(DataDirectoryResolver.Canonicalize(baseDir), DataDirectoryResolver.Canonicalize(aliased));
		} finally {
			Cleanup(baseDir);
		}
	}

	[Fact]
	public void D20_CanonicalizationIgnoresCaseForTheRoot() {
		var baseDir = TempBase();
		try {
			Directory.CreateDirectory(baseDir);
			var upper = baseDir.ToUpperInvariant();
			var lower = baseDir.ToLowerInvariant();

			Assert.Equal(DataDirectoryResolver.Canonicalize(upper), DataDirectoryResolver.Canonicalize(lower));
		} finally {
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D24_OwnershipIsExclusiveWhileAHostOwnsTheDirectory() {
		var baseDir = TempBase();
		try {
			var dataDirectory = Path.Combine(baseDir, "data");
			Databases.SetDataDirectoryForTest(dataDirectory);
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			await using (var db = Databases.Open<FakeDbContext>()) {
				db.Items.Add(new FakeItem { Name = "活跃" });
				await db.SaveChangesAsync();
			}
			await Databases.BackupNowAsync();
			var backupId = Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory).Single().BackupId;

			Assert.False(DataDirectoryOwnership.TryAcquire(dataDirectory, out _, out var failure));
			Assert.NotNull(failure);

			var result = await Databases.RestoreAsync("Fake", backupId);
			Assert.Equal(RestoreOutcome.Completed, result.Outcome);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D21_TwoProgramDirectories_ShareOneExternalDataDirectory() {
		var baseDir = TempBase();
		try {
			var external = Path.Combine(baseDir, "external-data");
			Databases.SetDataDirectoryForTest(external);
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			await using (var db = Databases.Open<FakeDbContext>()) {
				db.Items.Add(new FakeItem { Name = "共享外置数据" });
				await db.SaveChangesAsync();
			}
			SqliteConnection.ClearAllPools();

			Databases.SetDataDirectoryForTest(external);
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			await using (var db = Databases.Open<FakeDbContext>()) {
				var names = await db.Items.Select(item => item.Name).ToListAsync();
				Assert.Equal(["共享外置数据"], names);
			}
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D31_ShutdownWithoutPreparation_DoesNotCreateDatabases() {
		var baseDir = TempBase();
		try {
			Databases.SetDataDirectoryForTest(Path.Combine(baseDir, "data"));
			Databases.Init();

			var result = await Databases.ShutdownAsync();

			Assert.Equal(0, result.Total);
			Assert.True(result.Success);
			Assert.False(File.Exists(Path.Combine(baseDir, "data", "Fake.db")));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task D31_ShutdownIsIdempotentAndSharesTheSameResult() {
		var baseDir = TempBase();
		try {
			Databases.SetDataDirectoryForTest(Path.Combine(baseDir, "data"));
			Databases.Init();
			await Databases.PrepareAsync<FakeDbContext>();
			await using (var db = Databases.Open<FakeDbContext>()) {
				db.Items.Add(new FakeItem { Name = "终备份" });
				await db.SaveChangesAsync();
			}
			var before = Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory).Count;

			var first = await Databases.ShutdownAsync();
			var second = await Databases.ShutdownAsync();

			Assert.True(first.Success);
			Assert.Same(first, second);
			Assert.Equal(before + 1, Databases.ListBackups("Fake", DatabaseBackupStore.RollingCategory).Count);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}
}
