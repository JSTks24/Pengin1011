namespace Pengin1011.Core;

public sealed record DatabaseStatus(
	string Database,
	DatabaseState State,
	long? SizeBytes,
	long? WalSizeBytes,
	string? CurrentHistory,
	string? FilePath,
	DatabaseBackupInfo? LastCompletedBackup,
	DateTimeOffset? LastBackupAttemptUtc,
	bool? LastBackupAttemptSucceeded,
	bool PendingMaintenance,
	int CompletedBackupCount,
	IReadOnlyList<string> Notes,
	DatabaseProvider Provider = DatabaseProvider.SQLite);
