using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pengin1011.Tests;

#nullable disable

namespace Pengin1011.Tests.Migrations;

[DbContext(typeof(ClashDbContext))]
[Migration("20261014133527_Init")]
partial class ClashInit {
	protected override void BuildTargetModel(ModelBuilder modelBuilder) {
#pragma warning disable 612, 618
		modelBuilder.HasAnnotation("ProductVersion", "10.0.12");

		modelBuilder.Entity("Pengin1011.Tests.ClashItem", b => {
			b.Property<long>("Id")
				.ValueGeneratedOnAdd()
				.HasColumnType("INTEGER");

			b.HasKey("Id");

			b.ToTable("Clashes");
		});
#pragma warning restore 612, 618
	}
}
