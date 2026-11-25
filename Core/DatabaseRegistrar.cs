using Microsoft.EntityFrameworkCore;

namespace Pengin1011.Core;

public sealed class DatabaseRegistrar {
	private readonly DatabaseProvider _provider;
	internal List<DatabaseDefinition> Definitions { get; } = [];

	internal DatabaseRegistrar(DatabaseProvider provider = DatabaseProvider.SQLite) {
		_provider = provider;
	}

	public void Add<TContext>(string id, Func<DatabaseProvider, string, TContext> factory) where TContext : DbContext {
		if (!DatabaseIdentity.IsSafeIdentifier(id)) throw new ArgumentException(Loc.Get("DatabaseNameInvalid"), nameof(id));
		if (_provider != DatabaseProvider.SQLite) DatabaseOptions.ValidateExternalIdentifier(id);
		Definitions.Add(new DatabaseDefinition(id, typeof(TContext), connectionString => factory(_provider, connectionString)));
	}
}
