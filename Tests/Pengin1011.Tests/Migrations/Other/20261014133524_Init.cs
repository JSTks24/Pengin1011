using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pengin1011.Tests.Migrations;

public partial class OtherInit : Migration {
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
		migrationBuilder.DropTable(
			name: "Things");
	}
}
