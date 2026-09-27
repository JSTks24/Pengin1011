using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using QingQiu1011.Core;

namespace QingQiu1011.Tests;

public sealed class FakeItem {
	public long Id { get; set; }

	public string Name { get; set; } = "";

	public string? Note { get; set; }
}

public sealed class FakeDbContext : DbContext {
	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		Databases.Configure(GetType(), options);
	}

	public DbSet<FakeItem> Items => Set<FakeItem>();
}

[DbContext(typeof(FakeDbContext))]
[Migration("20260919000001_Init")]
public sealed class FakeInitMigration : Migration {
	protected override void Up(MigrationBuilder migrationBuilder) {
		migrationBuilder.CreateTable(
			name: "Items",
			columns: table => new {
				Id = table.Column<long>(type: "INTEGER", nullable: false)
					.Annotation("Sqlite:Autoincrement", true),
				Name = table.Column<string>(type: "TEXT", nullable: false)
			},
			constraints: table => {
				table.PrimaryKey("PK_Items", x => x.Id);
			});
	}

	protected override void Down(MigrationBuilder migrationBuilder) {
		migrationBuilder.DropTable(name: "Items");
	}
}

[DbContext(typeof(FakeDbContext))]
[Migration("20260919000002_AddNote")]
public sealed class FakeAddNoteMigration : Migration {
	protected override void Up(MigrationBuilder migrationBuilder) {
		migrationBuilder.AddColumn<string>(
			name: "Note",
			table: "Items",
			type: "TEXT",
			nullable: true);
	}

	protected override void Down(MigrationBuilder migrationBuilder) {
		migrationBuilder.DropColumn(name: "Note", table: "Items");
	}
}

public sealed class OtherItem {
	public long Id { get; set; }
}

public sealed class OtherDbContext : DbContext {
	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		Databases.Configure(GetType(), options);
	}

	public DbSet<OtherItem> Things => Set<OtherItem>();
}

[DbContext(typeof(OtherDbContext))]
[Migration("20260919000001_Init")]
public sealed class OtherInitMigration : Migration {
	protected override void Up(MigrationBuilder migrationBuilder) {
		migrationBuilder.CreateTable(
			name: "Things",
			columns: table => new {
				Id = table.Column<long>(type: "INTEGER", nullable: false)
					.Annotation("Sqlite:Autoincrement", true)
			},
			constraints: table => {
				table.PrimaryKey("PK_Things", x => x.Id);
			});
	}

	protected override void Down(MigrationBuilder migrationBuilder) {
		migrationBuilder.DropTable(name: "Things");
	}
}

public sealed class BareDbContext : DbContext {
	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		Databases.Configure(GetType(), options);
	}
}

public sealed class ClashDbContext : DbContext {
	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		Databases.Configure(GetType(), options);
	}
}

[DbContext(typeof(ClashDbContext))]
[Migration("20260919000001_Init")]
public sealed class ClashInitMigration : Migration {
	protected override void Up(MigrationBuilder migrationBuilder) {
		migrationBuilder.CreateTable(
			name: "Clash",
			columns: table => new {
				Id = table.Column<long>(type: "INTEGER", nullable: false)
					.Annotation("Sqlite:Autoincrement", true)
			},
			constraints: table => {
				table.PrimaryKey("PK_Clash", x => x.Id);
			});
	}

	protected override void Down(MigrationBuilder migrationBuilder) {
		migrationBuilder.DropTable(name: "Clash");
	}
}

public sealed class ClashContext : DbContext {
	protected override void OnConfiguring(DbContextOptionsBuilder options) {
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
