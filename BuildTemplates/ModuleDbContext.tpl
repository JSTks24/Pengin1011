using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pengin1011.Core;

namespace Pengin1011.Modules.__MODULE__;

public class __MODULE__DbContext : DbContext {
	public __MODULE__DbContext() {
	}

	public __MODULE__DbContext(DbContextOptions<__MODULE__DbContext> options) : base(options) {
	}

	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		if (options.IsConfigured) return;
		Databases.Configure(GetType(), options);
	}
}

public sealed class __MODULE__DbContextFactory : IDesignTimeDbContextFactory<__MODULE__DbContext> {
	public __MODULE__DbContext CreateDbContext(string[] args) {
		var options = new DbContextOptionsBuilder<__MODULE__DbContext>().UseSqlite("Data Source=__MODULE__-design.db").Options;
		return new __MODULE__DbContext(options);
	}
}
