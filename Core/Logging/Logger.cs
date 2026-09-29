using Pengin1011.Core.Localization;

namespace Pengin1011.Core.Logging;

public static class Logger {
	private static readonly object Gate = new();

	private static string? _errorLogPathOverride;
	private static string? _infoLogPathOverride;

	public static string LogPath => _errorLogPathOverride ?? Path.Combine(AppContext.BaseDirectory, "runtime", "logs", "error.log");
	public static string InfoPath => _infoLogPathOverride ?? Path.Combine(AppContext.BaseDirectory, "runtime", "logs", "info.log");

	internal static void SetLogPathForTest(string? path) {
		_errorLogPathOverride = path;
	}

	internal static void SetInfoPathForTest(string? path) {
		_infoLogPathOverride = path;
	}

	public static void Error(Type source, string message) {
		Write(Console.Error, LogPath, source, message, null);
	}

	public static void Error(Type source, Exception exception) {
		Write(Console.Error, LogPath, source, "", exception);
	}

	public static void Error(Type source, Exception exception, string message) {
		Write(Console.Error, LogPath, source, message, exception);
	}

	public static void Info(Type source, string message) {
		Write(Console.Out, InfoPath, source, message, null);
	}

	private static void Write(TextWriter console, string logPath, Type source, string message, Exception? exception) {
		var body = exception == null
			? message
			: message.Length > 0 ? $"{message}\n{exception}" : exception.ToString();
		var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source.Name}] {body}\n";
		lock (Gate) {
			console.WriteLine(entry);
			try {
				var dir = Path.GetDirectoryName(Path.GetFullPath(logPath));
				if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
				File.AppendAllText(logPath, entry + Environment.NewLine);
			} catch (Exception e) {
				Console.Error.WriteLine(Localizer.Format("LogFileWriteFailed", e.Message));
			}
		}
	}
}
