using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pengin1011.Core;

namespace Pengin1011.Tests;

public sealed class ShutdownItem {
	public long Id { get; set; }

	public string Name { get; set; } = "";
}

public sealed class ShutdownDbContext : DbContext {
	public ShutdownDbContext() {
	}

	public ShutdownDbContext(DbContextOptions<ShutdownDbContext> options) : base(options) {
	}

	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		if (options.IsConfigured) return;
		Databases.Configure(GetType(), options);
	}

	public DbSet<ShutdownItem> Items => Set<ShutdownItem>();
}

public sealed class ShutdownDbContextFactory : IDesignTimeDbContextFactory<ShutdownDbContext> {
	public ShutdownDbContext CreateDbContext(string[] args) {
		var options = new DbContextOptionsBuilder<ShutdownDbContext>().UseSqlite("Data Source=shutdown-design.db").Options;
		return new ShutdownDbContext(options);
	}
}
