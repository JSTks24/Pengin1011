namespace Pengin1011.Core;

public sealed record DatabasePathOptions(string? CommandLine, string? Configured, bool CreateIfMissing);

public static class DataDirectoryResolver {
	public static string Default => Path.Combine(AppContext.BaseDirectory, "data");

	public static DataDirectorySource Resolve(DatabasePathOptions options, out string directory, out string? failure) {
		var source = !string.IsNullOrWhiteSpace(options.CommandLine) ? DataDirectorySource.CommandLine
			: !string.IsNullOrWhiteSpace(options.Configured) ? DataDirectorySource.Configuration : DataDirectorySource.Default;
		var raw = source == DataDirectorySource.CommandLine ? options.CommandLine : options.Configured;
		try {
			directory = source == DataDirectorySource.Default ? Default
				: Path.GetFullPath(Path.IsPathRooted(raw!) ? raw! : Path.Combine(AppContext.BaseDirectory, raw!));
			failure = source != DataDirectorySource.Default && !options.CreateIfMissing && !Directory.Exists(directory) ? directory : null;
		} catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) {
			directory = "";
			failure = e.Message;
		}
		return source;
	}

	public static string Canonicalize(string directory) {
		var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
		return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
	}

	public static bool IsSameDirectory(string left, string right) {
		return string.Equals(Canonicalize(left), Canonicalize(right), StringComparison.Ordinal);
	}
}

public enum DataDirectorySource {
	CommandLine,
	Configuration,
	Default
}
