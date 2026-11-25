namespace Pengin1011.Tests;

internal sealed class ExternalDatabaseFactAttribute : FactAttribute {
	public ExternalDatabaseFactAttribute(params string[] variables) {
		var missing = variables.Where(variable => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable))).ToArray();
		if (missing.Length != 0) Skip = $"Set {string.Join(", ", missing)} to configuration JSON paths for a dedicated, pre-created test database.";
	}
}
