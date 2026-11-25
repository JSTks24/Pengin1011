namespace Pengin1011.Core;

public sealed record BackupResult(int Total, int Succeeded, IReadOnlyList<BackupFailure> Failures, bool RetentionWarning = false) {
	public bool Success => Failures.Count == 0 && Succeeded == Total;
}
