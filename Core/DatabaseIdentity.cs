namespace Pengin1011.Core;

public static class DatabaseIdentity {
	private static readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase) {
		"CON", "PRN", "AUX", "NUL",
		"COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
		"LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
	};

	public static bool TryDerive(Type contextType, out string id, out string? failure) {
		id = "";
		failure = null;
		var name = contextType.Name;
		if (name.EndsWith("DbContext", StringComparison.Ordinal)) name = name[..^9];
		else if (name.EndsWith("Context", StringComparison.Ordinal)) name = name[..^7];
		if (name.Length == 0) {
			failure = "ModuleNameDeriveFailed";
			return false;
		}
		if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar)) {
			failure = "DatabaseNameInvalid";
			return false;
		}
		if (name.IndexOfAny(['.', ' ']) >= 0) {
			failure = "DatabaseNameInvalid";
			return false;
		}
		if (_reserved.Contains(name)) {
			failure = "DatabaseNameReserved";
			return false;
		}
		id = name;
		return true;
	}

	public static string FileName(string id) {
		return $"{id}.db";
	}

	internal static bool IsSafeIdentifier(string value) {
		return !string.IsNullOrWhiteSpace(value) && !value.Contains("..", StringComparison.Ordinal)
			&& value.IndexOfAny(['/', '\\', ':']) < 0 && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
			&& !_reserved.Contains(value) && value == value.TrimEnd('.', ' ');
	}
}
