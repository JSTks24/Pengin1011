using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;
using Pengin1011.Helper;

namespace Pengin1011.Core;

public sealed record SnapshotMetadata {
	public required string DatabaseId { get; init; }
	public required string Category { get; init; }
	public required string BackupId { get; init; }
	public required DateTimeOffset CreatedUtc { get; init; }
	public required string OperationId { get; init; }
	public required IReadOnlyList<string> History { get; init; }
	public required long SizeBytes { get; init; }
	public required string Sha256 { get; init; }
	public string? ApplicationVersion { get; init; }
	public string? ApplicationCommit { get; init; }
	public string? UpgradeTarget { get; init; }
}

public static class DatabaseBackupStore {
	public const string RollingCategory = "rolling";
	public const string PreMigrationCategory = "pre-migration";
	public const string LegacyCategory = "legacy";
	public const int RollingKeepCount = 5;
	public const string SnapshotFileName = "snapshot.db";
	public const string MetadataFileName = "metadata.json";
	public const string PartialSuffix = ".partial";

	public static string DatabaseRoot(string dataDirectory, string databaseId) {
		return Path.Combine(dataDirectory, "backup", databaseId);
	}

	public static string CategoryRoot(string dataDirectory, string databaseId, string category) {
		return Path.Combine(DatabaseRoot(dataDirectory, databaseId), category);
	}

	public static string SnapshotPath(string dataDirectory, string databaseId, string category, string backupId) {
		return Path.Combine(CategoryRoot(dataDirectory, databaseId, category), backupId, SnapshotFileName);
	}

	public static bool TryCreate(string dataDirectory, string databaseId, string category, string sourceDatabasePath, IReadOnlyList<string> upgradeTarget, out string? backupId, out string? failure) {
		backupId = null;
		failure = null;
		if (!IsUsableSource(sourceDatabasePath, out var sourceFailure)) {
			failure = sourceFailure;
			return false;
		}
		var id = $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}"[..32];
		var categoryRoot = CategoryRoot(dataDirectory, databaseId, category);
		var partialDirectory = Path.Combine(categoryRoot, id + PartialSuffix);
		var finalDirectory = Path.Combine(categoryRoot, id);
		try {
			Directory.CreateDirectory(categoryRoot);
			Directory.CreateDirectory(partialDirectory);
			var snapshotPath = Path.Combine(partialDirectory, SnapshotFileName);
			using (var context = new SqliteConnection(SqliteConnectionPolicy.ForExistingDatabase(sourceDatabasePath))) {
				context.Open();
				using var command = context.CreateCommand();
				command.CommandText = "VACUUM INTO $target";
				command.Parameters.AddWithValue("$target", snapshotPath);
				command.ExecuteNonQuery();
			}
			using (var verification = SqliteConnectionPolicy.OpenReadOnly(snapshotPath)) {
				if (SqliteConnectionPolicy.ReadIntegrityCheck(verification) != 1) {
					failure = Loc.Get("BackupIntegrityFailed");
					return false;
				}
				if (SqliteConnectionPolicy.ReadForeignKeyCheck(verification) != 0) {
					failure = Loc.Get("BackupForeignKeyFailed");
					return false;
				}
			}
			var size = new FileInfo(snapshotPath).Length;
			var hash = HashFile(snapshotPath);
			var history = ReadHistory(snapshotPath);
			var metadata = new SnapshotMetadata {
				DatabaseId = databaseId,
				Category = category,
				BackupId = id,
				CreatedUtc = DateTimeOffset.UtcNow,
				OperationId = Guid.NewGuid().ToString("N"),
				History = history,
				SizeBytes = size,
				Sha256 = hash,
				ApplicationVersion = typeof(DatabaseBackupStore).Assembly.GetName().Version?.ToString(),
				UpgradeTarget = upgradeTarget.Count == 0 ? null : string.Join(",", upgradeTarget),
			};
			WriteMetadata(partialDirectory, metadata);
			FlushDirectory(partialDirectory);
			Directory.Move(partialDirectory, finalDirectory);
			FlushDirectory(categoryRoot);
			backupId = id;
			return true;
		} catch (Exception e) {
			Logger.Error(typeof(DatabaseBackupStore), e, Loc.Format("BackupFailed", databaseId));
			failure = e.Message;
			TryQuarantine(partialDirectory, id);
			return false;
		}
	}

	private static bool TryReadMetadata(string dataDirectory, string databaseId, string category, string backupId, out SnapshotMetadata? metadata, out string? failure) {
		metadata = null;
		failure = null;
		if (!DatabaseIdentity.IsSafeIdentifier(databaseId) || !DatabaseIdentity.IsSafeIdentifier(backupId)
			|| category is not (RollingCategory or PreMigrationCategory)) {
			failure = Loc.Format("BackupIdentityMismatch", backupId);
			return false;
		}
		var root = Path.Combine(CategoryRoot(dataDirectory, databaseId, category), backupId);
		try {
			metadata = JsonSerializer.Deserialize<SnapshotMetadata>(File.ReadAllText(Path.Combine(root, MetadataFileName)), JsonHelper.Options);
			if (metadata == null || metadata.DatabaseId != databaseId || metadata.Category != category || metadata.BackupId != backupId
				|| metadata.History == null || metadata.Sha256?.Length != 64 || !File.Exists(Path.Combine(root, SnapshotFileName))) {
				metadata = null;
				failure = Loc.Format("BackupIdentityMismatch", backupId);
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			Logger.Error(typeof(DatabaseBackupStore), e, Loc.Format("BackupUnreadable", backupId));
			failure = e.Message;
			metadata = null;
		}
		return metadata != null;
	}

	private static DatabaseBackupInfo Describe(string dataDirectory, SnapshotMetadata metadata) {
		return new DatabaseBackupInfo(metadata.BackupId, metadata.Category, metadata.CreatedUtc, metadata.History.LastOrDefault(),
			metadata.SizeBytes, SnapshotPath(dataDirectory, metadata.DatabaseId, metadata.Category, metadata.BackupId), metadata.History);
	}

	public static bool TryVerify(string dataDirectory, string databaseId, string category, string backupId, out DatabaseBackupInfo? info, out string? failure) {
		info = null;
		if (!TryReadMetadata(dataDirectory, databaseId, category, backupId, out var metadata, out failure)) return false;
		var candidate = Describe(dataDirectory, metadata!);
		if (!string.Equals(TryHashFile(candidate.SnapshotPath), metadata!.Sha256, StringComparison.OrdinalIgnoreCase)) {
			failure = Loc.Format("BackupHashMismatch", backupId);
			return false;
		}
		try {
			using var connection = SqliteConnectionPolicy.OpenReadOnly(candidate.SnapshotPath);
			if (SqliteConnectionPolicy.ReadIntegrityCheck(connection) != 1 || SqliteConnectionPolicy.ReadForeignKeyCheck(connection) != 0
				|| !ReadHistory(candidate.SnapshotPath).SequenceEqual(metadata.History)) {
				failure = Loc.Get("BackupIntegrityFailed");
				return false;
			}
		} catch (Exception e) when (e is IOException or SqliteException) {
			Logger.Error(typeof(DatabaseBackupStore), e, Loc.Format("BackupUnreadable", backupId));
			failure = e.Message;
			return false;
		}
		info = candidate;
		return true;
	}

	public static IReadOnlyList<DatabaseBackupInfo> ListCompleted(string dataDirectory, string databaseId, string category) {
		var root = CategoryRoot(dataDirectory, databaseId, category);
		var list = new List<DatabaseBackupInfo>();
		if (!Directory.Exists(root)) return list;
		foreach (var directory in Directory.EnumerateDirectories(root)) {
			var id = Path.GetFileName(directory);
			if (id.EndsWith(PartialSuffix, StringComparison.Ordinal)) continue;
			if (TryReadMetadata(dataDirectory, databaseId, category, id, out var metadata, out _)) list.Add(Describe(dataDirectory, metadata!));
		}
		list.Sort(static (left, right) => {
			var byTime = left.CreatedUtc.CompareTo(right.CreatedUtc);
			return byTime != 0 ? byTime : string.CompareOrdinal(left.BackupId, right.BackupId);
		});
		return list;
	}

	public static IReadOnlyList<string> ListLegacy(string dataDirectory, string databaseId) {
		var root = Path.Combine(dataDirectory, "backup");
		if (!DatabaseIdentity.IsSafeIdentifier(databaseId) || !Directory.Exists(root)) return [];
		return [.. Directory.EnumerateFiles(root, databaseId + "-*.db", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).Where(name => name != null).Select(name => name!)];
	}

	public static bool TryPruneRolling(string dataDirectory, string databaseId, out string? failure) {
		return TryPruneRolling(dataDirectory, databaseId, null, out failure);
	}

	internal static bool TryPruneRolling(string dataDirectory, string databaseId, string? protectedBackupId, out string? failure) {
		failure = null;
		try {
			var completed = ListCompleted(dataDirectory, databaseId, RollingCategory);
			if (completed.Count <= RollingKeepCount) return true;
			var root = CategoryRoot(dataDirectory, databaseId, RollingCategory);
			foreach (var stale in completed.Take(completed.Count - RollingKeepCount)) {
				if (stale.BackupId == protectedBackupId) continue;
				try {
					Directory.Delete(Path.Combine(root, stale.BackupId), true);
				} catch (Exception e) {
					Logger.Error(typeof(DatabaseBackupStore), e, Loc.Format("BackupPruneFailed", stale.BackupId));
					failure = e.Message;
				}
			}
		} catch (Exception e) {
			Logger.Error(typeof(DatabaseBackupStore), e, Loc.Format("BackupPruneFailed", databaseId));
			failure = e.Message;
		}
		return failure == null;
	}

	public static string HashFile(string path) {
		using var stream = File.OpenRead(path);
		return Convert.ToHexString(SHA256.HashData(stream));
	}

	private static string? TryHashFile(string path) {
		try {
			return HashFile(path);
		} catch (IOException e) {
			Logger.Error(typeof(DatabaseBackupStore), e, Loc.Format("BackupUnreadable", Path.GetFileName(path)));
			return null;
		}
	}

	internal static bool HasTrackedSchema(string databasePath, IReadOnlyList<string> history) {
		return history.Count > 0 || !DatabasePrepareEngine.HasUserTables(databasePath);
	}

	public static IReadOnlyList<string> ReadHistory(string databasePath) {
		var history = new List<string>();
		try {
			using var connection = SqliteConnectionPolicy.OpenReadOnly(databasePath);
			using var command = connection.CreateCommand();
			command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'";
			if (command.ExecuteScalar() == null) return history;
			using var reader = connection.CreateCommand();
			reader.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId";
			using var result = reader.ExecuteReader();
			while (result.Read()) history.Add(result.GetString(0));
		} catch (SqliteException e) {
			Logger.Error(typeof(DatabaseBackupStore), e, Loc.Format("DatabasePrepareFailed", Path.GetFileName(databasePath)));
			throw;
		}
		return history;
	}

	private static void WriteMetadata(string directory, SnapshotMetadata metadata) {
		var path = Path.Combine(directory, MetadataFileName);
		var json = JsonSerializer.Serialize(metadata, JsonHelper.Options);
		using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) {
			using var writer = new StreamWriter(stream);
			writer.Write(json);
			writer.Flush();
			stream.Flush(true);
		}
	}

	public static void FlushDirectory(string directory) {
		try {
			using var handle = File.OpenHandle(directory, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.None);
			RandomAccess.FlushToDisk(handle);
		} catch (Exception e) {
			Logger.Info(typeof(DatabaseBackupStore), Loc.Format("DirectoryFlushSkipped", directory, e.GetType().Name));
		}
	}

	private static void TryQuarantine(string partialDirectory, string id) {
		try {
			if (!Directory.Exists(partialDirectory)) return;
			var quarantine = partialDirectory + ".failed";
			if (Directory.Exists(quarantine)) Directory.Delete(quarantine, true);
			Directory.Move(partialDirectory, quarantine);
		} catch (Exception e) {
			Logger.Error(typeof(DatabaseBackupStore), e, Loc.Format("BackupQuarantineFailed", id));
		}
	}

	private static bool IsUsableSource(string path, out string? failure) {
		failure = null;
		if (!File.Exists(path)) {
			failure = Loc.Format("BackupSourceMissing", Path.GetFileNameWithoutExtension(path));
			return false;
		}
		try {
			using var connection = SqliteConnectionPolicy.OpenReadOnly(path);
			if (SqliteConnectionPolicy.ReadIntegrityCheck(connection) != 1) {
				failure = Loc.Format("BackupSourceNotSqlite", Path.GetFileName(path));
				return false;
			}
		} catch (SqliteException e) {
			failure = Loc.Format("BackupSourceNotSqlite", Path.GetFileName(path)) + ": " + e.Message;
			return false;
		}
		return true;
	}

}
