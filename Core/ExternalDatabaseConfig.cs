namespace Pengin1011.Core;

public sealed class ExternalDatabaseConfig {
	public string Host { get; init; } = "";
	public int? Port { get; init; }
	public string Database { get; init; } = "";
	public string Username { get; init; } = "";
	public string Password { get; init; } = "";
}
