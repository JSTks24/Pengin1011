namespace Pengin1011.Core;

public sealed class DatabaseConfig {
	public string? DataDirectory { get; init; }
	public ExternalDatabaseConfig? MySQL { get; init; }
	public ExternalDatabaseConfig? PostgreSQL { get; init; }
}
