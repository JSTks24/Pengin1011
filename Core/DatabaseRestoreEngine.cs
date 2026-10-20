using Pengin1011.Core.Logging;

namespace Pengin1011.Core;

internal static class DatabaseRestoreEngine {
	internal static readonly string[] FileSuffixes = ["", "-wal", "-shm", "-journal"];
	internal static Action<string>? BoundaryForTest;

	public static Task<RestoreResult> RestoreAsync(string dataDirectory, string databaseId, string backupId, CancellationToken ct) {
		return Task.Run(() => Restore(dataDirectory, databaseId, backupId, ct), ct);
	}

	private static RestoreResult Restore(string directory, string id, string backupId, CancellationToken ct) {
		if (!DatabaseIdentity.IsSafeIdentifier(id) || !DatabaseIdentity.IsSafeIdentifier(backupId)) {
			return new RestoreResult(RestoreOutcome.Rejected, null, Loc.Format("RestoreBackupIdInvalid", backupId));
		}
		MaintenanceJournal.RestoreRecord? record = null;
		try {
			if (MaintenanceJournal.ListUnfinishedRestores(directory, id).Count > 0) {
				return new RestoreResult(RestoreOutcome.Rejected, null, Loc.Format("DatabaseMaintenanceRequired", id));
			}
			var migration = MaintenanceJournal.ReadMigrationIntent(directory, id);
			DatabaseBackupInfo? backup = null;
			foreach (var category in new[] { DatabaseBackupStore.RollingCategory, DatabaseBackupStore.PreMigrationCategory }) {
				if (DatabaseBackupStore.TryVerify(directory, id, category, backupId, out backup, out _)) break;
			}
			if (backup == null) return new RestoreResult(RestoreOutcome.Rejected, null, Loc.Format("RestoreBackupInvalid", backupId, ""));
			if (!DatabaseBackupStore.HasTrackedSchema(backup.SnapshotPath, backup.History)) {
				return new RestoreResult(RestoreOutcome.Rejected, null, Loc.Format("DatabaseUntrackedTables", id));
			}
			var livePath = Path.Combine(directory, DatabaseIdentity.FileName(id));
			record = new MaintenanceJournal.RestoreRecord {
				FormatVersion = 2,
				OperationId = MaintenanceJournal.CreateOperationId(),
				Database = id,
				SourceBackupId = backupId,
				SourceCategory = backup.Category,
				TargetSha256 = DatabaseBackupStore.HashFile(backup.SnapshotPath),
				TargetHistory = backup.History,
				OriginalMigrationOperationId = migration?.OperationId,
				OriginalFiles = FileSuffixes.Select(suffix => Describe(livePath, suffix)).ToArray(),
				State = "Preparing",
				StartedUtc = DateTimeOffset.UtcNow
			};
			MaintenanceJournal.WriteRestoreRecord(directory, id, record);
			var operation = MaintenanceJournal.OperationDirectory(directory, id, record.OperationId);
			var quarantine = Path.Combine(operation, MaintenanceJournal.QuarantineDirectoryName);
			Directory.CreateDirectory(quarantine);
			foreach (var file in record.OriginalFiles.Where(file => file.Exists)) {
				ct.ThrowIfCancellationRequested();
				BoundaryForTest?.Invoke("BeforeQuarantineCopy" + file.Suffix);
				CopyDurably(livePath + file.Suffix, Path.Combine(quarantine, Path.GetFileName(livePath) + file.Suffix));
				BoundaryForTest?.Invoke("QuarantineCopied" + file.Suffix);
			}
			VerifyFiles(record, quarantine, id);
			var staging = Path.Combine(operation, MaintenanceJournal.StagingFileName);
			CopyDurably(backup.SnapshotPath, staging);
			BoundaryForTest?.Invoke("StagingCopied");
			VerifyTarget(record, staging);
			record = Save(directory, record with { State = "Ready" });
			ct.ThrowIfCancellationRequested();
			var result = Cutover(directory, record, staging, ct);
			return new RestoreResult(result.Success ? RestoreOutcome.Completed : RestoreOutcome.Failed, record.OperationId, result.Reason);
		} catch (Exception e) {
			Logger.Error(typeof(DatabaseRestoreEngine), e, Loc.Format("RestoreFailed", id, record?.OperationId ?? "-"));
			if (record != null) SaveFailure(directory, record, e);
			return new RestoreResult(RestoreOutcome.Failed, record?.OperationId, e.Message);
		}
	}

	public static MaintenanceResult Complete(string directory, string id, string operationId) {
		try {
			var record = MaintenanceJournal.ReadRestoreRecord(directory, id, operationId);
			if (record == null) return new MaintenanceResult(false, Loc.Format("RestoreOperationUnknown", operationId));
			return CompleteCore(directory, record, CancellationToken.None);
		} catch (Exception e) {
			Logger.Error(typeof(DatabaseRestoreEngine), e, Loc.Format("RestoreFailed", id, operationId));
			return new MaintenanceResult(false, e.Message, operationId);
		}
	}

	private static MaintenanceResult CompleteCore(string directory, MaintenanceJournal.RestoreRecord record, CancellationToken ct) {
		var id = record.Database;
		var operationId = record.OperationId;
		var live = Path.Combine(directory, DatabaseIdentity.FileName(id));
		if (record.State is "RolledBack" or "Cancelled" or "RollbackStarted" or "Preparing") {
			return new MaintenanceResult(false, Loc.Format("RestorePhaseRejected", record.State), operationId);
		}
		try {
			if (record.State == "Completed") {
				VerifyTarget(record, live);
				ClearOriginalMigrationIntent(directory, record);
				return new MaintenanceResult(true, null, operationId);
			}
			var operation = MaintenanceJournal.OperationDirectory(directory, id, operationId);
			var quarantine = Path.Combine(operation, MaintenanceJournal.QuarantineDirectoryName);
			VerifyFiles(record, quarantine, id);
			if (!DatabaseBackupStore.TryVerify(directory, id, record.SourceCategory, record.SourceBackupId, out var backup, out var failure) || backup == null) {
				return new MaintenanceResult(false, failure, operationId);
			}
			if (!DatabaseBackupStore.HasTrackedSchema(backup.SnapshotPath, backup.History)) {
				return new MaintenanceResult(false, Loc.Format("DatabaseUntrackedTables", id), operationId);
			}
			var staging = Path.Combine(operation, MaintenanceJournal.StagingFileName);
			CopyDurably(backup.SnapshotPath, staging);
			BoundaryForTest?.Invoke("StagingCopied");
			VerifyTarget(record, staging);
			return Cutover(directory, record, staging, ct);
		} catch (Exception e) {
			Logger.Error(typeof(DatabaseRestoreEngine), e, Loc.Format("RestoreFailed", id, operationId));
			SaveFailure(directory, record, e);
			return new MaintenanceResult(false, e.Message, operationId);
		}
	}

	private static MaintenanceResult Cutover(string directory, MaintenanceJournal.RestoreRecord record, string staging, CancellationToken ct) {
		var live = Path.Combine(directory, DatabaseIdentity.FileName(record.Database));
		record = Save(directory, record with { State = "CutoverStarted", Failure = null });
		ct.ThrowIfCancellationRequested();
		foreach (var suffix in FileSuffixes.Skip(1)) {
			if (File.Exists(live + suffix)) File.Delete(live + suffix);
		}
		BoundaryForTest?.Invoke("SidecarsMoved");
		ct.ThrowIfCancellationRequested();
		File.Move(staging, live, true);
		BoundaryForTest?.Invoke("MainReplaced");
		DatabaseBackupStore.FlushDirectory(directory);
		VerifyTarget(record, live);
		Save(directory, record with { State = "Completed", CompletedUtc = DateTimeOffset.UtcNow.ToString("O") });
		ClearOriginalMigrationIntent(directory, record);
		return new MaintenanceResult(true, null, record.OperationId);
	}

	private static void ClearOriginalMigrationIntent(string directory, MaintenanceJournal.RestoreRecord record) {
		var intent = MaintenanceJournal.ReadMigrationIntent(directory, record.Database);
		if (intent == null) return;
		if (intent.OperationId != record.OriginalMigrationOperationId) throw new InvalidOperationException(Loc.Format("DatabaseMaintenanceRequired", record.Database));
		MaintenanceJournal.ClearMigrationIntent(directory, record.Database);
	}

	public static MaintenanceResult Rollback(string directory, string id, string operationId) {
		MaintenanceJournal.RestoreRecord? record = null;
		try {
			record = MaintenanceJournal.ReadRestoreRecord(directory, id, operationId);
			if (record == null) return new MaintenanceResult(false, Loc.Format("RestoreOperationUnknown", operationId));
			if (record.State == "Completed") return new MaintenanceResult(false, Loc.Format("RestoreAlreadyCompleted", operationId));
			var live = Path.Combine(directory, DatabaseIdentity.FileName(id));
			if (record.State is "Preparing" or "Ready" or "Cancelled") {
				VerifyFiles(record, directory, id);
				Save(directory, record with { State = "Cancelled", Failure = null, CompletedUtc = DateTimeOffset.UtcNow.ToString("O") });
				return new MaintenanceResult(true, null, operationId);
			}
			if (record.State == "RolledBack") {
				VerifyFiles(record, directory, id);
				return new MaintenanceResult(true, null, operationId);
			}
			var quarantine = Path.Combine(MaintenanceJournal.OperationDirectory(directory, id, operationId), MaintenanceJournal.QuarantineDirectoryName);
			VerifyFiles(record, quarantine, id);
			record = Save(directory, record with { State = "RollbackStarted", Failure = null });
			foreach (var file in record.OriginalFiles) {
				var target = live + file.Suffix;
				if (file.Exists) {
					var temporary = target + ".rollback";
					CopyDurably(Path.Combine(quarantine, Path.GetFileName(target)), temporary);
					File.Move(temporary, target, true);
				} else {
					File.Delete(target);
				}
				BoundaryForTest?.Invoke("RollbackCopied" + file.Suffix);
			}
			VerifyFiles(record, directory, id);
			DatabaseBackupStore.FlushDirectory(directory);
			Save(directory, record with { State = "RolledBack", Failure = null, CompletedUtc = DateTimeOffset.UtcNow.ToString("O") });
			return new MaintenanceResult(true, null, operationId);
		} catch (Exception e) {
			Logger.Error(typeof(DatabaseRestoreEngine), e, Loc.Format("RestoreRollbackFailed", operationId));
			if (record != null) SaveFailure(directory, record, e);
			return new MaintenanceResult(false, e.Message, operationId);
		}
	}

	private static MaintenanceJournal.OriginalFile Describe(string live, string suffix) {
		var path = live + suffix;
		return File.Exists(path) ? new(suffix, true, new FileInfo(path).Length, DatabaseBackupStore.HashFile(path)) : new(suffix, false, 0, null);
	}

	private static void VerifyFiles(MaintenanceJournal.RestoreRecord record, string directory, string id) {
		foreach (var file in record.OriginalFiles) {
			var actual = Describe(Path.Combine(directory, DatabaseIdentity.FileName(id)), file.Suffix);
			if (actual != file) throw new InvalidDataException(Loc.Get("RestoreQuarantineMismatch"));
		}
	}

	private static void VerifyTarget(MaintenanceJournal.RestoreRecord record, string path) {
		if (!File.Exists(path) || DatabaseBackupStore.HashFile(path) != record.TargetSha256) {
			throw new InvalidDataException(Loc.Get("RestoreTargetMismatch"));
		}
		using var connection = SqliteConnectionPolicy.OpenReadOnly(path);
		if (SqliteConnectionPolicy.ReadIntegrityCheck(connection) != 1 || SqliteConnectionPolicy.ReadForeignKeyCheck(connection) != 0
			|| !DatabaseBackupStore.ReadHistory(path).SequenceEqual(record.TargetHistory)) {
			throw new InvalidDataException(Loc.Get("RestoreStagingInvalid"));
		}
	}

	private static void CopyDurably(string source, string destination) {
		using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
		using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
		input.CopyTo(output);
		output.Flush(true);
	}

	private static MaintenanceJournal.RestoreRecord Save(string directory, MaintenanceJournal.RestoreRecord record) {
		MaintenanceJournal.WriteRestoreRecord(directory, record.Database, record);
		BoundaryForTest?.Invoke(record.State);
		return record;
	}

	private static void SaveFailure(string directory, MaintenanceJournal.RestoreRecord record, Exception failure) {
		try {
			var persisted = MaintenanceJournal.ReadRestoreRecord(directory, record.Database, record.OperationId);
			if (persisted != null) MaintenanceJournal.WriteRestoreRecord(directory, record.Database, persisted with { Failure = failure.Message });
		} catch (Exception e) {
			Logger.Error(typeof(DatabaseRestoreEngine), e, Loc.Format("MaintenanceRecordReadFailed", record.OperationId));
		}
	}
}
