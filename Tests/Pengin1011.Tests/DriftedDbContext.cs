using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pengin1011.Core;

namespace Pengin1011.Tests;

public sealed class DriftItem {
	public long Id { get; set; }

	public string Name { get; set; } = "";
}

// 模型相对其 ModelSnapshot 已经漂移：OnModelCreating 始终多出一个 DriftedColumn，
// 而 Migrations/Drift 下的快照是在该列加入之前生成的（因此没有 pending migration）。
// 这正是任务书 D02 要求的场景：无 pending migration 也必须检出模型漂移。
public sealed class DriftedDbContext : DbContext {
	public DriftedDbContext() {
	}

	public DriftedDbContext(DbContextOptions<DriftedDbContext> options) : base(options) {
	}

	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		if (options.IsConfigured) return;
		Databases.Configure(GetType(), options);
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder) {
		base.OnModelCreating(modelBuilder);
		modelBuilder.Entity<DriftItem>().Property<string>("DriftedColumn");
	}

	public DbSet<DriftItem> DriftItems => Set<DriftItem>();
}

public sealed class DriftedDbContextFactory : IDesignTimeDbContextFactory<DriftedDbContext> {
	public DriftedDbContext CreateDbContext(string[] args) {
		var options = new DbContextOptionsBuilder<DriftedDbContext>().UseSqlite("Data Source=drift-design.db").Options;
		return new DriftedDbContext(options);
	}
}
