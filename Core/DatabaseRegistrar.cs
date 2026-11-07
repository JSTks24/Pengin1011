using Microsoft.EntityFrameworkCore;

namespace Pengin1011.Core;

public sealed class DatabaseRegistrar {
	internal List<DatabaseDefinition> Definitions { get; } = [];

	public void Add<TContext>(string id, Func<DbContextOptions<TContext>, TContext> factory) where TContext : DbContext {
		if (!DatabaseIdentity.IsSafeIdentifier(id)) throw new ArgumentException(Loc.Get("DatabaseNameInvalid"), nameof(id));
		Definitions.Add(new DatabaseDefinition(id, typeof(TContext), connectionString => {
			var options = new DbContextOptionsBuilder<TContext>().UseSqlite(connectionString);
			options.AddInterceptors(SqliteConnectionPolicy.Interceptor);
			return factory(options.Options);
		}));
	}
}
