using Microsoft.EntityFrameworkCore;

namespace Pengin1011.Tests;

public sealed class ClashDbContext : DbContext {
	public ClashDbContext() {
	}

	public ClashDbContext(DbContextOptions<ClashDbContext> options) : base(options) {
	}


	public DbSet<ClashItem> Clashes => Set<ClashItem>();
}
