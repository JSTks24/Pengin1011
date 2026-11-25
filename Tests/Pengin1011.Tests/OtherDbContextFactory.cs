using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pengin1011.Tests;

public sealed class OtherDbContextFactory : IDesignTimeDbContextFactory<OtherDbContext> {
	public OtherDbContext CreateDbContext(string[] args) {
		var options = new DbContextOptionsBuilder<OtherDbContext>().UseSqlite("Data Source=other-design.db").Options;
		return new OtherDbContext(options);
	}
}
