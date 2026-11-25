using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pengin1011.Tests;

public sealed class ShutdownDbContextFactory : IDesignTimeDbContextFactory<ShutdownDbContext> {
	public ShutdownDbContext CreateDbContext(string[] args) {
		var options = new DbContextOptionsBuilder<ShutdownDbContext>().UseSqlite("Data Source=shutdown-design.db").Options;
		return new ShutdownDbContext(options);
	}
}
