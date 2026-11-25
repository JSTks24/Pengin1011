using Microsoft.EntityFrameworkCore;
using Pengin1011.Core;

namespace Pengin1011.Modules.FakeModule;

[ModuleDatabase("FakeModule")]
public partial class FakeModuleDbContext : DbContext {
	public DbSet<FakeItem> Items => Set<FakeItem>();
}
