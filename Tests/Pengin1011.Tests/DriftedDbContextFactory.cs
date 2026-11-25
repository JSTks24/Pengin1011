using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pengin1011.Tests;

public sealed class DriftedDbContextFactory : IDesignTimeDbContextFactory<DriftedDbContext> {
	public DriftedDbContext CreateDbContext(string[] args) {
		var options = new DbContextOptionsBuilder<DriftedDbContext>().UseSqlite("Data Source=drift-design.db").Options;
		return new DriftedDbContext(options);
	}
}
