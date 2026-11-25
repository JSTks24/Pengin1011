using Microsoft.EntityFrameworkCore;

namespace Pengin1011.Tests;

public sealed class BareDbContext : DbContext {
	public BareDbContext() {
	}

	public BareDbContext(DbContextOptions<BareDbContext> options) : base(options) {
	}

}
