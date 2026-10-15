using System.Text.Json;
using Pengin1011.Core.Logging;
using Pengin1011.Helper;

namespace Pengin1011.Core;

public static class MaintenanceJournal {
	internal static Action<string, string>? BeforePublishForTest;
	public const string DirectoryName = "maintenance";
	public const string RecordFileName = "operation.json";
	public const string MigrationIntentFileName = "migration-intent.json";
	public const string QuarantineDirectoryName = "quarantine";
	public const string StagingFileName = "staging.db";

	public sealed record MigrationIntent {
		public required string Database { get; init; }
		public required string OperationId { get; init; }
		public required string State { get; init; }
		public required IReadOnlyList<string> PreviousHistory { get; init; }
		public required IReadOnlyList<string> TargetHistory { get; init; }
		public string? SnapshotId { get; init; }
		public bool FreshDatabase { get; init; }
		public required DateTimeOffset UpdatedUtc { get; init; }
	}

	public sealed record OriginalFile(string Suffix, bool Exists, long Length, string? Sha256);

	public sealed record RestoreRecord {
		public int FormatVersion { get; init; }
		public required string OperationId { get; init; }
		public required string Database { get; init; }
		public required string SourceBackupId { get; init; }
		public string SourceCategory { get; init; } = "";
		public string TargetSha256 { get; init; } = "";
		public IReadOnlyList<string> TargetHistory { get; init; } = [];
		public string? OriginalMigrationOperationId { get; init; }
		public IReadOnlyList<OriginalFile> OriginalFiles { get; init; } = [];
		public required string State { get; init; }
		public required DateTimeOffset StartedUtc { get; init; }
		public string? CompletedUtc { get; init; }
		public string? Failure { get; init; }
	}

	public static string OperationRoot(string dataDirectory, string databaseId) {
		ValidateId(databaseId);
		return Path.Combine(dataDirectory, "backup", databaseId, DirectoryName);
	}

	public static string OperationDirectory(string dataDirectory, string databaseId, string operationId) {
		ValidateId(operationId);
		return Path.Combine(OperationRoot(dataDirectory, databaseId), operationId);
	}

	public static string MigrationIntentPath(string dataDirectory, string databaseId) {
		return Path.Combine(OperationRoot(dataDirectory, databaseId), MigrationIntentFileName);
	}

	public static string CreateOperationId() => $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}"[..32];

	public static void WriteMigrationIntent(string dataDirectory, string databaseId, MigrationIntent intent) {
		WriteJson(MigrationIntentPath(dataDirectory, databaseId), intent);
	}

	public static MigrationIntent? ReadMigrationIntent(string dataDirectory, string databaseId) {
		var path = MigrationIntentPath(dataDirectory, databaseId);
		var value = ReadJson<MigrationIntent>(path);
		if (value != null && (value.Database != databaseId || !DatabaseIdentity.IsSafeIdentifier(value.OperationId)
			|| value.PreviousHistory == null || value.TargetHistory == null)) throw InvalidRecord(path);
		return value;
	}

	public static void ClearMigrationIntent(string dataDirectory, string databaseId) {
		File.Delete(MigrationIntentPath(dataDirectory, databaseId));
	}

	public static void WriteRestoreRecord(string dataDirectory, string databaseId, RestoreRecord record) {
		WriteJson(Path.Combine(OperationDirectory(dataDirectory, databaseId, record.OperationId), RecordFileName), record);
	}

	public static RestoreRecord? ReadRestoreRecord(string dataDirectory, string databaseId, string operationId) {
		var path = Path.Combine(OperationDirectory(dataDirectory, databaseId, operationId), RecordFileName);
		var record = ReadJson<RestoreRecord>(path);
		if (record == null) return null;
		if (record.Database != databaseId || record.OperationId != operationId || record.FormatVersion != 2
			|| record.TargetHistory == null || record.OriginalFiles == null
			|| record.OriginalFiles.Count != 4 || record.OriginalFiles.Any(file => file == null)
			|| record.OriginalFiles.Select(file => file.Suffix).Distinct().Count() != 4
			|| record.OriginalFiles.Any(file => !DatabaseRestoreEngine.FileSuffixes.Contains(file.Suffix)
				|| file.Exists && (file.Length < 0 || file.Sha256?.Length != 64))
			|| record.OriginalMigrationOperationId != null && !DatabaseIdentity.IsSafeIdentifier(record.OriginalMigrationOperationId)
			|| record.TargetSha256?.Length != 64
			|| record.State is not ("Preparing" or "Ready" or "CutoverStarted" or "RollbackStarted" or "Completed" or "RolledBack" or "Cancelled")) {
			throw InvalidRecord(path);
		}
		return record;
	}

	public static IReadOnlyList<RestoreRecord> ListUnfinishedRestores(string dataDirectory, string databaseId) {
		var root = OperationRoot(dataDirectory, databaseId);
		if (!Directory.Exists(root)) return [];
		var list = new List<RestoreRecord>();
		foreach (var directory in Directory.EnumerateDirectories(root)) {
			var operationId = Path.GetFileName(directory);
			if (IsLegacyTerminal(Path.Combine(directory, RecordFileName), databaseId, operationId)) continue;
			var record = ReadRestoreRecord(dataDirectory, databaseId, operationId);
			if (record == null) throw InvalidRecord(Path.Combine(directory, RecordFileName));
			if (record.State is not ("Completed" or "RolledBack" or "Cancelled")) list.Add(record);
		}
		return list;
	}

	private static bool IsLegacyTerminal(string path, string databaseId, string operationId) {
		using var document = ReadJson<JsonDocument>(path) ?? throw InvalidRecord(path);
		var root = document.RootElement;
		if (root.ValueKind != JsonValueKind.Object) return false;
		if (root.TryGetProperty("FormatVersion", out var version) && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 0)) return false;
		return DatabaseIdentity.IsSafeIdentifier(operationId)
			&& root.TryGetProperty("Database", out var database) && database.ValueKind == JsonValueKind.String && database.GetString() == databaseId
			&& root.TryGetProperty("OperationId", out var operation) && operation.ValueKind == JsonValueKind.String && operation.GetString() == operationId
			&& root.TryGetProperty("State", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() is "Completed" or "RolledBack"
			&& root.TryGetProperty("TargetHistory", out var history) && history.ValueKind == JsonValueKind.String
			&& root.TryGetProperty("OriginalDatabasePath", out var original) && original.ValueKind == JsonValueKind.String
			&& root.TryGetProperty("OriginalSidecars", out var sidecars) && sidecars.ValueKind == JsonValueKind.Array
			&& root.TryGetProperty("Stages", out var stages) && stages.ValueKind == JsonValueKind.Array;
	}

	private static void WriteJson<T>(string path, T value) {
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try {
			using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
				JsonSerializer.Serialize(stream, value, JsonHelper.Options);
				stream.Flush(true);
			}
			BeforePublishForTest?.Invoke(path, temporary);
			File.Move(temporary, path, true);
			DatabaseBackupStore.FlushDirectory(Path.GetDirectoryName(path)!);
		} finally {
			if (File.Exists(temporary)) File.Delete(temporary);
		}
	}

	private static T? ReadJson<T>(string path) where T : class {
		try {
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			return JsonSerializer.Deserialize<T>(stream, JsonHelper.Options) ?? throw InvalidRecord(path);
		} catch (FileNotFoundException) {
			return null;
		} catch (DirectoryNotFoundException) {
			return null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			Logger.Error(typeof(MaintenanceJournal), e, Loc.Format("MaintenanceRecordReadFailed", path));
			throw new InvalidDataException(Loc.Format("MaintenanceRecordReadFailed", path), e);
		}
	}

	private static InvalidDataException InvalidRecord(string path) => new(Loc.Format("MaintenanceRecordReadFailed", path));

	private static void ValidateId(string value) {
		if (!DatabaseIdentity.IsSafeIdentifier(value)) throw new ArgumentException(Loc.Get("DatabaseNameInvalid"), nameof(value));
	}
}
