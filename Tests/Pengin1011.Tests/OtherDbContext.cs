using Microsoft.EntityFrameworkCore;

namespace Pengin1011.Tests;

public sealed class OtherDbContext : DbContext {
	public OtherDbContext() {
	}

	public OtherDbContext(DbContextOptions<OtherDbContext> options) : base(options) {
	}


	public DbSet<OtherItem> Things => Set<OtherItem>();
}
