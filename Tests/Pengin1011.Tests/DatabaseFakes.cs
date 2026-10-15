using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Pengin1011.Core;

namespace Pengin1011.Tests;

public sealed class FakeItem {
	public long Id { get; set; }

	public string Name { get; set; } = "";

	public string? Note { get; set; }
}

public sealed class FakeDbContext : DbContext {
	public FakeDbContext() {
	}

	public FakeDbContext(DbContextOptions<FakeDbContext> options) : base(options) {
	}

	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		if (options.IsConfigured) return;
		Databases.Configure(GetType(), options);
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder) {
		modelBuilder.Entity<FakeItem>().HasIndex(item => item.Name);
	}

	public DbSet<FakeItem> Items => Set<FakeItem>();
}

public sealed class FakeDbContextFactory : IDesignTimeDbContextFactory<FakeDbContext> {
	public FakeDbContext CreateDbContext(string[] args) {
		var options = new DbContextOptionsBuilder<FakeDbContext>().UseSqlite("Data Source=fake-design.db").Options;
		return new FakeDbContext(options);
	}
}

public sealed class OtherItem {
	public long Id { get; set; }
}

public sealed class OtherDbContext : DbContext {
	public OtherDbContext() {
	}

	public OtherDbContext(DbContextOptions<OtherDbContext> options) : base(options) {
	}

	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		if (options.IsConfigured) return;
		Databases.Configure(GetType(), options);
	}

	public DbSet<OtherItem> Things => Set<OtherItem>();
}

public sealed class OtherDbContextFactory : IDesignTimeDbContextFactory<OtherDbContext> {
	public OtherDbContext CreateDbContext(string[] args) {
		var options = new DbContextOptionsBuilder<OtherDbContext>().UseSqlite("Data Source=other-design.db").Options;
		return new OtherDbContext(options);
	}
}

public sealed class BareDbContext : DbContext {
	public BareDbContext() {
	}

	public BareDbContext(DbContextOptions<BareDbContext> options) : base(options) {
	}

	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		if (options.IsConfigured) return;
		Databases.Configure(GetType(), options);
	}
}

public sealed class ClashItem {
	public long Id { get; set; }
}

public sealed class ClashDbContext : DbContext {
	public ClashDbContext() {
	}

	public ClashDbContext(DbContextOptions<ClashDbContext> options) : base(options) {
	}

	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		if (options.IsConfigured) return;
		Databases.Configure(GetType(), options);
	}

	public DbSet<ClashItem> Clashes => Set<ClashItem>();
}

public sealed class ClashDbContextFactory : IDesignTimeDbContextFactory<ClashDbContext> {
	public ClashDbContext CreateDbContext(string[] args) {
		var options = new DbContextOptionsBuilder<ClashDbContext>().UseSqlite("Data Source=clash-design.db").Options;
		return new ClashDbContext(options);
	}
}

public sealed class ClashContext : DbContext {
	public ClashContext() {
	}

	public ClashContext(DbContextOptions<ClashContext> options) : base(options) {
	}

	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		if (options.IsConfigured) return;
		Databases.Configure(GetType(), options);
	}
}

public sealed class RawSqlContext : DbContext {
	private readonly string _path;

	public RawSqlContext(string path) {
		_path = path;
	}

	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		options.UseSqlite($"Data Source={_path}");
	}
}
