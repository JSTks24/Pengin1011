using Microsoft.EntityFrameworkCore;
using QingQiu1011.Core;

namespace QingQiu1011.Modules.__MODULE__;

public class __MODULE__DbContext : DbContext {
	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		Databases.Configure(GetType(), options);
	}
}
