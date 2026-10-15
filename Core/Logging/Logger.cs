using Pengin1011.Core.Localization;

namespace Pengin1011.Core.Logging;

public enum LogLevel {
	Info,
	Warn,
	Error,
}

public sealed record LogEntry(DateTime Time, LogLevel Level, Type Source, string Message, Exception? Exception);

public static class Logger {
	private static readonly object _gate = new();
	private static readonly object _subscribeGate = new();
	private static SubscriptionEntry[] _subscribers = [];

	private static string? _errorLogPathOverride;
	private static string? _infoPathOverride;

	public static string LogPath => _errorLogPathOverride ?? Path.Combine(AppContext.BaseDirectory, "runtime", "logs", "error.log");
	public static string InfoPath => _infoPathOverride ?? Path.Combine(AppContext.BaseDirectory, "runtime", "logs", "info.log");

	internal static void SetLogPathForTest(string? path) {
		_errorLogPathOverride = path;
	}

	internal static void SetInfoPathForTest(string? path) {
		_infoPathOverride = path;
	}

	public static IDisposable Subscribe(Action<LogEntry> handler) {
		ArgumentNullException.ThrowIfNull(handler);
		var subscription = new SubscriptionEntry(handler);
		lock (_subscribeGate) {
			_subscribers = [.. _subscribers, subscription];
		}
		return new Subscription(() => {
			lock (_subscribeGate) {
				_subscribers = _subscribers.Where(existing => !ReferenceEquals(existing, subscription)).ToArray();
			}
		});
	}

	internal static int SubscriberCountForTest {
		get {
			lock (_subscribeGate) return _subscribers.Length;
		}
	}

	public static void Error(Type source, string message) {
		Write(Console.Error, LogPath, LogLevel.Error, source, message, null);
	}

	public static void Error(Type source, Exception exception) {
		Write(Console.Error, LogPath, LogLevel.Error, source, "", exception);
	}

	public static void Error(Type source, Exception exception, string message) {
		Write(Console.Error, LogPath, LogLevel.Error, source, message, exception);
	}

	public static void Warn(Type source, string message) {
		Write(Console.Error, LogPath, LogLevel.Warn, source, message, null);
	}

	public static void Info(Type source, string message) {
		Write(Console.Out, InfoPath, LogLevel.Info, source, message, null);
	}

	private static void Write(TextWriter console, string logPath, LogLevel level, Type source, string message, Exception? exception) {
		var body = exception == null
			? message
			: message.Length > 0 ? $"{message}\n{exception}" : exception.ToString();
		var time = DateTime.Now;
		var entry = $"[{time:yyyy-MM-dd HH:mm:ss.fff}] [{source.Name}] {body}\n";
		lock (_gate) {
			console.WriteLine(entry);
			try {
				var dir = Path.GetDirectoryName(Path.GetFullPath(logPath));
				if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
				File.AppendAllText(logPath, entry + Environment.NewLine);
			} catch (Exception e) {
				Console.Error.WriteLine(Loc.Format("LogFileWriteFailed", e.Message));
			}
		}
		Publish(new LogEntry(time, level, source, message, exception));
	}

	private static void Publish(LogEntry entry) {
		var batch = _subscribers;
		if (batch.Length == 0) return;
		foreach (var subscription in batch) {
			try {
				subscription.Handler(entry);
			} catch (Exception e) {
				Console.Error.WriteLine(Loc.Format("LogSubscriberFailed", entry.Source.Name, e));
			}
		}
	}

	internal sealed class SubscriptionEntry(Action<LogEntry> handler) {
		public Action<LogEntry> Handler { get; } = handler;
	}

	private sealed class Subscription(Action dispose) : IDisposable {
		private Action? _dispose = dispose;

		public void Dispose() {
			var action = _dispose;
			_dispose = null;
			action?.Invoke();
		}
	}
}
