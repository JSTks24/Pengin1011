using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pengin1011.Tests.Migrations;

public partial class DriftInit : Migration {
	protected override void Up(MigrationBuilder migrationBuilder) {
		migrationBuilder.CreateTable(
			name: "DriftItems",
			columns: table => new {
				Id = table.Column<long>(type: "INTEGER", nullable: false)
					.Annotation("Sqlite:Autoincrement", true),
				Name = table.Column<string>(type: "TEXT", nullable: false)
			},
			constraints: table => {
				table.PrimaryKey("PK_DriftItems", x => x.Id);
			});
	}

	protected override void Down(MigrationBuilder migrationBuilder) {
		migrationBuilder.DropTable(
			name: "DriftItems");
	}
}
