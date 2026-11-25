namespace Pengin1011.Core;

internal sealed record SnapshotMetadata {
	public required string DatabaseId { get; init; }
	public required string Category { get; init; }
	public required string BackupId { get; init; }
	public required DateTimeOffset CreatedUtc { get; init; }
	public required IReadOnlyList<string> History { get; init; }
	public required long SizeBytes { get; init; }
	public required string Sha256 { get; init; }
	public string? ApplicationVersion { get; init; }
	public string? UpgradeTarget { get; init; }
}
