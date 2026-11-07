using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pengin1011.Core;

namespace Pengin1011.Modules.FakeModule;

public sealed class FakeItem {
	public long Id { get; set; }

	public string Name { get; set; } = "";
}

[ModuleDatabase("FakeModule")]
public partial class FakeModuleDbContext : DbContext {
	public DbSet<FakeItem> Items => Set<FakeItem>();
}
