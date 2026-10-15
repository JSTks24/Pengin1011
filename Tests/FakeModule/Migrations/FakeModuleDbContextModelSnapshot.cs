using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pengin1011.Modules.FakeModule;

#nullable disable

namespace Pengin1011.Modules.FakeModule.Migrations;

[DbContext(typeof(FakeModuleDbContext))]
partial class FakeModuleDbContextModelSnapshot : ModelSnapshot {
	protected override void BuildModel(ModelBuilder modelBuilder) {
#pragma warning disable 612, 618
		modelBuilder.HasAnnotation("ProductVersion", "10.0.12");

		modelBuilder.Entity("Pengin1011.Modules.FakeModule.FakeItem", b => {
			b.Property<long>("Id")
				.ValueGeneratedOnAdd()
				.HasColumnType("INTEGER");

			b.Property<string>("Name")
				.IsRequired()
				.HasColumnType("TEXT");

			b.HasKey("Id");

			b.ToTable("Items");
		});
#pragma warning restore 612, 618
	}
}
