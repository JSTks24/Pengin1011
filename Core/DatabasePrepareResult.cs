namespace Pengin1011.Core;

internal sealed record DatabasePrepareResult(bool Success, DatabaseState State, string? Failure);
