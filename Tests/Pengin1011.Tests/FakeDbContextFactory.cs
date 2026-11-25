using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pengin1011.Tests;

public sealed class FakeDbContextFactory : IDesignTimeDbContextFactory<FakeDbContext> {
	public FakeDbContext CreateDbContext(string[] args) {
		var options = new DbContextOptionsBuilder<FakeDbContext>().UseSqlite("Data Source=fake-design.db").Options;
		return new FakeDbContext(options);
	}
}
