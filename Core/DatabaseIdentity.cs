namespace Pengin1011.Core;

internal static class DatabaseIdentity {
	private static readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase) {
		"CON", "PRN", "AUX", "NUL",
		"COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
		"LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
	};

	public static string FileName(string id) {
		return $"{id}.db";
	}

	internal static bool IsSafeIdentifier(string value) {
		return !string.IsNullOrWhiteSpace(value) && !value.Contains("..", StringComparison.Ordinal)
			&& value.IndexOfAny(['/', '\\', ':']) < 0 && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
			&& !_reserved.Contains(value) && value == value.TrimEnd('.', ' ');
	}
}
