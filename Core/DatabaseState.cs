namespace Pengin1011.Core;

public enum DatabaseState {
	NotPresent,
	Unprepared,
	Preparing,
	Prepared,
	Failed,
	MaintenanceRequired,
}

public sealed record DatabaseBackupInfo(
	string BackupId,
	string Category,
	DateTimeOffset CreatedUtc,
	string? LatestMigration,
	long SizeBytes,
	string SnapshotPath,
	IReadOnlyList<string> History);

public sealed record DatabaseStatus(
	string Database,
	DatabaseState State,
	long SizeBytes,
	long WalSizeBytes,
	string? CurrentHistory,
	string? FilePath,
	DatabaseBackupInfo? LastCompletedBackup,
	DateTimeOffset? LastBackupAttemptUtc,
	bool? LastBackupAttemptSucceeded,
	bool PendingMaintenance,
	int CompletedBackupCount,
	IReadOnlyList<string> Notes);

internal sealed record DatabasePrepareResult(bool Success, DatabaseState State, string? Failure);

internal enum RestoreOutcome {
	Completed,
	Rejected,
	Failed,
}

internal sealed record RestoreResult(RestoreOutcome Outcome, string? OperationId, string? Reason);
