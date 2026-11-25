namespace Pengin1011.Core;

public sealed record DatabaseBackupInfo(
	string BackupId,
	string Category,
	DateTimeOffset CreatedUtc,
	string? LatestMigration,
	long SizeBytes,
	string SnapshotPath,
	IReadOnlyList<string> History);
