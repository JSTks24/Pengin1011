using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pengin1011.Core;

namespace Pengin1011.Modules.FakeModule;

public sealed class FakeItem {
	public long Id { get; set; }

	public string Name { get; set; } = "";
}

public class FakeModuleDbContext : DbContext {
	public FakeModuleDbContext() {
	}

	public FakeModuleDbContext(DbContextOptions<FakeModuleDbContext> options) : base(options) {
	}

	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		if (options.IsConfigured) return;
		Databases.Configure(GetType(), options);
	}

	public DbSet<FakeItem> Items => Set<FakeItem>();
}

public sealed class FakeModuleDbContextFactory : IDesignTimeDbContextFactory<FakeModuleDbContext> {
	public FakeModuleDbContext CreateDbContext(string[] args) {
		var options = new DbContextOptionsBuilder<FakeModuleDbContext>().UseSqlite("Data Source=fakemodule-design.db").Options;
		return new FakeModuleDbContext(options);
	}
}
