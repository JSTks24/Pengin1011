using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pengin1011.Tests;

#nullable disable

namespace Pengin1011.Tests.Migrations;

[DbContext(typeof(OtherDbContext))]
[Migration("20261014133524_Init")]
partial class OtherInit {
	protected override void BuildTargetModel(ModelBuilder modelBuilder) {
#pragma warning disable 612, 618
		modelBuilder.HasAnnotation("ProductVersion", "10.0.12");

		modelBuilder.Entity("Pengin1011.Tests.OtherItem", b => {
			b.Property<long>("Id")
				.ValueGeneratedOnAdd()
				.HasColumnType("INTEGER");

			b.HasKey("Id");

			b.ToTable("Things");
		});
#pragma warning restore 612, 618
	}
}
