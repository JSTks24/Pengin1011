using Microsoft.EntityFrameworkCore;
using Pengin1011.Core;

namespace Pengin1011.Modules.__MODULE__;

public class __MODULE__DbContext : DbContext {
	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		Databases.Configure(GetType(), options);
	}
}
