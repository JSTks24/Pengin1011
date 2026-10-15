namespace Pengin1011.Core;

public enum DatabaseState {
	NotPresent,
	Unprepared,
	Preparing,
	Prepared,
	Failed,
	MaintenanceRequired,
}

public enum MaintenanceOperationState {
	Migrating,
	Completed,
}

public sealed record MaintenanceOperationRecord {
	public required string OperationId { get; init; }
	public required string Database { get; init; }
	public required string Category { get; init; }
	public required string State { get; init; }
	public required DateTimeOffset StartedUtc { get; init; }
	public string? CompletedUtc { get; init; }
	public string? PreviousHistory { get; init; }
	public string? TargetHistory { get; init; }
	public string? SnapshotId { get; init; }
	public bool FreshDatabase { get; init; }
	public string? SourceBackupId { get; init; }
	public string? Reason { get; init; }
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
	MaintenanceOperationRecord? PendingMaintenance,
	int CompletedBackupCount,
	IReadOnlyList<string> Notes);

public sealed record DatabasePrepareResult(bool Success, DatabaseState State, string? Failure);

public enum RestoreOutcome {
	Completed,
	Rejected,
	Failed,
}

public sealed record RestoreResult(RestoreOutcome Outcome, string? OperationId, string? Reason);

public sealed record MaintenanceResult(bool Success, string? Reason, string? OperationId = null);
