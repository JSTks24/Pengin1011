using Pengin1011.Core.Logging;

namespace Pengin1011.Core;

internal static class DatabaseRestoreEngine {
	internal static readonly string[] FileSuffixes = ["", "-wal", "-shm", "-journal"];
	internal static Action<string>? BoundaryForTest;

	internal static Task<RestoreResult> RestoreAsync(string directory, string id, string backupId, CancellationToken ct) {
		return Task.Run(() => Restore(directory, id, backupId, ct), ct);
	}

	private static RestoreResult Restore(string directory, string id, string backupId, CancellationToken ct) {
		if (!DatabaseIdentity.IsSafeIdentifier(id) || !DatabaseIdentity.IsSafeIdentifier(backupId)) {
			return new RestoreResult(RestoreOutcome.Rejected, null, Loc.Format("RestoreBackupIdInvalid", backupId));
		}
		string? operation = null;
		try {
			DatabaseBackupInfo? backup = null;
			foreach (var category in new[] { DatabaseBackupStore.RollingCategory, DatabaseBackupStore.PreMigrationCategory }) {
				if (DatabaseBackupStore.TryVerify(directory, id, category, backupId, out backup, out _)) break;
			}
			if (backup == null || !DatabaseBackupStore.HasTrackedSchema(backup.SnapshotPath, backup.History)) {
				return new RestoreResult(RestoreOutcome.Rejected, null, Loc.Format("RestoreBackupInvalid", backupId, ""));
			}
			var hash = DatabaseBackupStore.HashFile(backup.SnapshotPath);
			operation = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
			var work = Path.Combine(DatabaseBackupStore.DatabaseRoot(directory, id), "restore", operation);
			Directory.CreateDirectory(work);
			var staging = Path.Combine(work, "staging.db");
			CopyDurably(backup.SnapshotPath, staging);
			Verify(staging, hash, backup.History);
			BoundaryForTest?.Invoke("StagingCopied");
			var live = Path.Combine(directory, DatabaseIdentity.FileName(id));
			var original = Path.Combine(work, "original");
			Directory.CreateDirectory(original);
			foreach (var suffix in FileSuffixes) {
				ct.ThrowIfCancellationRequested();
				var source = live + suffix;
				try {
					File.GetAttributes(source);
				} catch (FileNotFoundException) {
					continue;
				} catch (DirectoryNotFoundException) {
					continue;
				}
				BoundaryForTest?.Invoke("BeforeOriginalCopy" + suffix);
				var copy = Path.Combine(original, Path.GetFileName(source));
				CopyDurably(source, copy);
				if (DatabaseBackupStore.HashFile(source) != DatabaseBackupStore.HashFile(copy)) throw new IOException(Loc.Get("RestoreOriginalMismatch"));
			}
			DatabaseBackupStore.FlushDirectory(original);
			ct.ThrowIfCancellationRequested();
			DatabaseMaintenance.Write(directory, id, "restore " + operation + " " + backupId);
			BoundaryForTest?.Invoke("MarkerWritten");
			ct.ThrowIfCancellationRequested();
			foreach (var suffix in FileSuffixes.Skip(1)) File.Delete(live + suffix);
			BoundaryForTest?.Invoke("SidecarsRemoved");
			ct.ThrowIfCancellationRequested();
			File.Move(staging, live, true);
			DatabaseBackupStore.FlushDirectory(directory);
			BoundaryForTest?.Invoke("MainReplaced");
			Verify(live, hash, backup.History);
			ct.ThrowIfCancellationRequested();
			DatabaseMaintenance.Clear(directory, id);
			return new RestoreResult(RestoreOutcome.Completed, operation, null);
		} catch (Exception e) {
			Logger.Error(typeof(DatabaseRestoreEngine), e, Loc.Format("RestoreFailed", id, operation ?? "-"));
			return new RestoreResult(RestoreOutcome.Failed, operation, e.Message);
		}
	}

	private static void Verify(string path, string hash, IReadOnlyList<string> history) {
		if (DatabaseBackupStore.HashFile(path) != hash) throw new InvalidDataException(Loc.Get("RestoreTargetMismatch"));
		using var connection = SqliteConnectionPolicy.OpenReadOnly(path);
		if (SqliteConnectionPolicy.ReadIntegrityCheck(connection) != 1 || SqliteConnectionPolicy.ReadForeignKeyCheck(connection) != 0
			|| !DatabaseBackupStore.ReadHistory(path).SequenceEqual(history)) throw new InvalidDataException(Loc.Get("RestoreStagingInvalid"));
	}

	private static void CopyDurably(string source, string destination) {
		using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
		using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
		input.CopyTo(output);
		output.Flush(true);
	}
}
