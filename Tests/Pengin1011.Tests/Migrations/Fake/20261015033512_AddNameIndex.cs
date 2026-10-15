using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pengin1011.Tests.Migrations;

public partial class AddNameIndex : Migration {
	protected override void Up(MigrationBuilder migrationBuilder) {
		migrationBuilder.CreateIndex(
			name: "IX_Items_Name",
			table: "Items",
			column: "Name");
	}

	protected override void Down(MigrationBuilder migrationBuilder) {
		migrationBuilder.DropIndex(
			name: "IX_Items_Name",
			table: "Items");
	}
}
