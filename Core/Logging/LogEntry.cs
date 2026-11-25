namespace Pengin1011.Core.Logging;

public sealed record LogEntry(DateTime Time, LogLevel Level, Type Source, string Message, Exception? Exception);
