using Microsoft.EntityFrameworkCore;

namespace Pengin1011.Tests;

public sealed class FakeDbContext : DbContext {
	public FakeDbContext() {
	}

	public FakeDbContext(DbContextOptions<FakeDbContext> options) : base(options) {
	}


	protected override void OnModelCreating(ModelBuilder modelBuilder) {
		modelBuilder.Entity<FakeItem>().HasIndex(item => item.Name);
	}

	public DbSet<FakeItem> Items => Set<FakeItem>();
}
