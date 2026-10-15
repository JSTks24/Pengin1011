using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class DriftProbeTests {
	[Fact]
	public void HasPendingModelChanges_WithNoPendingMigrationButADriftedModel_IsTrue() {
		var options = new DbContextOptionsBuilder<DriftedDbContext>().UseSqlite("Data Source=:memory:").Options;
		using var context = new DriftedDbContext(options);

		var migrations = context.Database.GetMigrations().ToList();
		var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;

		Assert.NotEmpty(migrations);
		Assert.NotNull(snapshot);
		Assert.True(context.Database.HasPendingModelChanges(), "模型已漂移，即使没有 pending migration 也必须检出");
	}

	[Fact]
	public void HasPendingModelChanges_ForAMatchingModel_IsFalse() {
		var options = new DbContextOptionsBuilder<FakeDbContext>().UseSqlite("Data Source=:memory:").Options;
		using var context = new FakeDbContext(options);

		Assert.False(context.Database.HasPendingModelChanges(), "模型与快照一致时不应误报漂移");
	}
}
