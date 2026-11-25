using Microsoft.EntityFrameworkCore;

namespace Pengin1011.Tests;

public sealed class ShutdownDbContext : DbContext {
	public ShutdownDbContext() {
	}

	public ShutdownDbContext(DbContextOptions<ShutdownDbContext> options) : base(options) {
	}


	public DbSet<ShutdownItem> Items => Set<ShutdownItem>();
}
