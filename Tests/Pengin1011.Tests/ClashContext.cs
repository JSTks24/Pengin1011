using Microsoft.EntityFrameworkCore;

namespace Pengin1011.Tests;

public sealed class ClashContext : DbContext {
	public ClashContext() {
	}

	public ClashContext(DbContextOptions<ClashContext> options) : base(options) {
	}

}
