namespace Pengin1011.Core;

internal sealed record ConfigError(string Message, Exception? Exception = null);
