using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pengin1011.Tests;

public sealed class ClashDbContextFactory : IDesignTimeDbContextFactory<ClashDbContext> {
	public ClashDbContext CreateDbContext(string[] args) {
		var options = new DbContextOptionsBuilder<ClashDbContext>().UseSqlite("Data Source=clash-design.db").Options;
		return new ClashDbContext(options);
	}
}
