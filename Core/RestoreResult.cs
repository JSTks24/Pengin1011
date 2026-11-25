namespace Pengin1011.Core;

internal sealed record RestoreResult(RestoreOutcome Outcome, string? OperationId, string? Reason);
