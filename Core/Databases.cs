using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;

namespace Pengin1011.Core;

public static class Databases {
	public const int BackupIntervalMinutes = 30;

	private static readonly DatabaseService _service = new();
	private static readonly object _gate = new();
	private static readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);

	private static Timer? _backupTimer;
	private static bool _shutdown;
	private static BackupResult? _finalBackup;
	private static readonly Dictionary<string, DateTimeOffset?> _lastBackupAttempts = new(StringComparer.OrdinalIgnoreCase);
	private static readonly Dictionary<string, bool> _lastBackupOutcomes = new(StringComparer.OrdinalIgnoreCase);

	public static string DataDirectory => _service.DataDirectory;

	public static bool DataDirectoryConfigured => _service.DataDirectoryConfigured;

	internal static SemaphoreSlim BackupGate => _service.MaintenanceGate;

	public static void ConfigureDataDirectory(string dataDirectory) {
		_service.Configure(dataDirectory);
	}

	public static bool TryResolveDataDirectory(DatabasePathOptions options, out string directory, out string? failure) {
		DataDirectoryResolver.Resolve(options, out directory, out failure);
		return failure == null;
	}

	internal static void ReleaseOwnership() => _service.ReleaseOwnership();
	internal static void AcquireOwnership() => _service.AcquireOwnership();

	public static void Init() {
		lock (_gate) {
			if (!_service.DataDirectoryConfigured) _service.Configure(DataDirectoryResolver.Default);
			_service.AcquireOwnership();
			foreach (var file in Directory.EnumerateFiles(_service.DataDirectory, "*.db", SearchOption.TopDirectoryOnly)) {
				_known.Add(Path.GetFileNameWithoutExtension(file));
			}
			_backupTimer ??= new Timer(OnBackupTimer, null, TimeSpan.FromMinutes(BackupIntervalMinutes), TimeSpan.FromMinutes(BackupIntervalMinutes));
		}
	}

	public static void Register<TContext>() where TContext : DbContext, new() {
		if (!DatabaseIdentity.TryDerive(typeof(TContext), out var id, out var failure)) {
			throw new ArgumentException(Loc.Get(failure ?? "DatabaseNameInvalid"), typeof(TContext).Name);
		}
		lock (_gate) {
			_known.Add(id);
		}
	}

	public static async Task PrepareAsync<TContext>(CancellationToken ct = default) where TContext : DbContext, new() {
		DatabasePrepareResult result;
		try {
			result = await _service.PrepareAsync<TContext>(ct);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			var cancelledId = DatabaseIdentity.TryDerive(typeof(TContext), out var pendingId, out _) ? pendingId : typeof(TContext).Name;
			Logger.Info(typeof(Databases), Loc.Format("DatabasePrepareCancelled", cancelledId));
			throw;
		}
		if (!result.Success) {
			var message = result.Failure ?? Loc.Format("DatabasePrepareFailed", typeof(TContext).Name);
			Logger.Error(typeof(Databases), message);
			throw new InvalidOperationException(message);
		}
		if (DatabaseIdentity.TryDerive(typeof(TContext), out var id, out _)) {
			lock (_gate) {
				_known.Add(id);
			}
		}
	}

	public static DatabaseState StateOf<TContext>() where TContext : DbContext, new() {
		return _service.StateOf<TContext>();
	}

	public static TContext Open<TContext>() where TContext : DbContext, new() {
		if (!DatabaseIdentity.TryDerive(typeof(TContext), out var id, out var deriveFailure)) {
			var message = Loc.Get(deriveFailure ?? "DatabaseNameInvalid");
			Logger.Error(typeof(Databases), message);
			throw new ArgumentException(message, typeof(TContext).Name);
		}
		var state = _service.StateOf(id);
		if (state != DatabaseState.Prepared) {
			var message = Loc.Format("DatabasePrepareRequired", id);
			Logger.Error(typeof(Databases), message);
			throw new InvalidOperationException(message);
		}
		_service.EnsureOwnership(id, typeof(TContext));
		Directory.CreateDirectory(_service.DataDirectory);
		lock (_gate) {
			_known.Add(id);
		}
		return new TContext();
	}

	public static void Configure(Type contextType, DbContextOptionsBuilder options) {
		options.UseSqlite(_service.ConnectionStringFor(contextType, creating: false));
		options.AddInterceptors(SqliteConnectionPolicy.Interceptor);
	}

	public static async Task<BackupResult> BackupNowAsync(CancellationToken ct = default) {
		_service.AcquireOwnership();
		await _service.MaintenanceGate.WaitAsync(ct);
		try {
			return BackupAllCore();
		} finally {
			_service.MaintenanceGate.Release();
		}
	}

	public static IReadOnlyList<DatabaseStatus> GetStatus() {
		var list = new List<DatabaseStatus>();
		foreach (var id in Ids()) {
			list.Add(BuildStatus(id));
		}
		return list;
	}

	public static DatabaseStatus GetStatus(string id) {
		return BuildStatus(id);
	}

	public static IReadOnlyList<string> DatabaseIds() {
		return [.. Ids()];
	}

	public static IReadOnlyList<DatabaseBackupInfo> ListBackups(string id, string category) {
		return DatabaseBackupStore.ListCompleted(_service.DataDirectory, id, category);
	}

	public static IReadOnlyList<string> ListLegacyBackups(string id) {
		return DatabaseBackupStore.ListLegacy(_service.DataDirectory, id);
	}

	internal static bool TryImportLegacyBackup(string id, string legacyPath, out string? importedBackupId, out string? failure) {
		_service.AcquireOwnership();
		importedBackupId = null;
		failure = null;
		if (!File.Exists(legacyPath)) {
			failure = Loc.Format("LegacyBackupMissing", legacyPath);
			return false;
		}
		var path = Path.Combine(_service.DataDirectory, DatabaseIdentity.FileName(id));
		if (string.Equals(Path.GetFullPath(legacyPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) {
			failure = Loc.Get("LegacyBackupIsLiveDatabase");
			return false;
		}
		try {
			using var verify = SqliteConnectionPolicy.OpenReadOnly(legacyPath);
			if (SqliteConnectionPolicy.ReadIntegrityCheck(verify) != 1) {
				failure = Loc.Get("RestoreVerificationIntegrity");
				return false;
			}
		} catch (Microsoft.Data.Sqlite.SqliteException e) {
			failure = Loc.Format("LegacyBackupNotSqlite", legacyPath) + ": " + e.Message;
			return false;
		}
		return DatabaseBackupStore.TryCreate(_service.DataDirectory, id, DatabaseBackupStore.RollingCategory, legacyPath, [], out importedBackupId, out failure);
	}

	internal static async Task<RestoreResult> RestoreAsync(string id, string backupId, CancellationToken ct = default) {
		_service.AcquireOwnership();
		await _service.MaintenanceGate.WaitAsync(ct);
		try {
			var result = await DatabaseRestoreEngine.RestoreAsync(_service.DataDirectory, id, backupId, ct);
			if (result.Outcome == RestoreOutcome.Completed) _service.ResetPrepareState();
			return result;
		} finally {
			_service.MaintenanceGate.Release();
		}
	}

	internal static MaintenanceResult RollbackRestore(string id, string operationId) {
		_service.AcquireOwnership();
		_service.MaintenanceGate.Wait();
		try {
			var result = DatabaseRestoreEngine.Rollback(_service.DataDirectory, id, operationId);
			if (result.Success) _service.ResetPrepareState();
			return result;
		} finally {
			_service.MaintenanceGate.Release();
		}
	}

	internal static MaintenanceResult CompleteRestore(string id, string operationId) {
		_service.AcquireOwnership();
		_service.MaintenanceGate.Wait();
		try {
			var result = DatabaseRestoreEngine.Complete(_service.DataDirectory, id, operationId);
			if (result.Success) _service.ResetPrepareState();
			return result;
		} finally {
			_service.MaintenanceGate.Release();
		}
	}

	internal static MaintenanceResult Unlock(string id) {
		_service.AcquireOwnership();
		if (MaintenanceJournal.ListUnfinishedRestores(DataDirectory, id).Count > 0) return new MaintenanceResult(false, Loc.Format("DatabaseMaintenanceRequired", id));
		var path = Path.Combine(_service.DataDirectory, DatabaseIdentity.FileName(id));
		if (!File.Exists(path)) return new MaintenanceResult(false, Loc.Format("DatabaseMissing", id));
		if (MaintenanceJournal.ReadMigrationIntent(_service.DataDirectory, id) != null) {
			return new MaintenanceResult(false, Loc.Format("UnlockWithPendingIntent", id));
		}
		if (!DatabasePrepareEngine.HasResidualMigrationLock(path)) {
			return new MaintenanceResult(true, Loc.Get("UnlockNothingToDo"));
		}
		try {
			using var connection = SqliteConnectionPolicy.OpenExisting(path);
			using var command = connection.CreateCommand();
			command.CommandText = "DELETE FROM __EFMigrationsLock";
			command.ExecuteNonQuery();
			return new MaintenanceResult(true, null);
		} catch (SqliteException e) {
			Logger.Error(typeof(Databases), e, Loc.Format("UnlockFailed", id));
			return new MaintenanceResult(false, e.Message);
		}
	}

	internal static MaintenanceResult Verify(string id, bool acceptInterrupted) {
		_service.AcquireOwnership();
		if (MaintenanceJournal.ListUnfinishedRestores(DataDirectory, id).Count > 0) return new MaintenanceResult(false, Loc.Format("DatabaseMaintenanceRequired", id));
		var path = Path.Combine(_service.DataDirectory, DatabaseIdentity.FileName(id));
		if (!File.Exists(path)) return new MaintenanceResult(false, Loc.Format("DatabaseMissing", id));
		if (!acceptInterrupted) return new MaintenanceResult(false, Loc.Get("VerifyRequiresConfirmation"));
		var intent = MaintenanceJournal.ReadMigrationIntent(_service.DataDirectory, id);
		using (var connection = SqliteConnectionPolicy.OpenReadOnly(path)) {
			if (SqliteConnectionPolicy.ReadIntegrityCheck(connection) != 1) {
				return new MaintenanceResult(false, Loc.Get("RestoreVerificationIntegrity"));
			}
			if (SqliteConnectionPolicy.ReadForeignKeyCheck(connection) != 0) {
				return new MaintenanceResult(false, Loc.Get("RestoreVerificationForeignKey"));
			}
		}
		if (intent != null && intent.State != nameof(MaintenanceOperationState.Completed)) {
			var history = DatabaseBackupStore.ReadHistory(path);
			if (!history.SequenceEqual(intent.TargetHistory)) {
				return new MaintenanceResult(false, Loc.Format("VerifyTargetUnknown", id));
			}
			MaintenanceJournal.ClearMigrationIntent(_service.DataDirectory, id);
		}
		return new MaintenanceResult(true, null, intent?.OperationId);
	}

	public static IReadOnlyList<MaintenanceOperationRecord> PendingMaintenance(string id) {
		var list = new List<MaintenanceOperationRecord>();
		var intent = MaintenanceJournal.ReadMigrationIntent(_service.DataDirectory, id);
		if (intent != null) {
			list.Add(new MaintenanceOperationRecord {
				OperationId = intent.OperationId,
				Database = id,
				Category = "migration",
				State = intent.State,
				StartedUtc = intent.UpdatedUtc,
				PreviousHistory = string.Join(",", intent.PreviousHistory),
				TargetHistory = string.Join(",", intent.TargetHistory),
				SnapshotId = intent.SnapshotId,
				FreshDatabase = intent.FreshDatabase,
			});
		}
		foreach (var restore in MaintenanceJournal.ListUnfinishedRestores(_service.DataDirectory, id)) {
			list.Add(new MaintenanceOperationRecord {
				OperationId = restore.OperationId,
				Database = id,
				Category = "restore",
				State = restore.State,
				StartedUtc = restore.StartedUtc,
				SourceBackupId = restore.SourceBackupId,
				TargetHistory = string.Join(",", restore.TargetHistory),
				Reason = restore.Failure,
			});
		}
		return list;
	}

	public static async Task<BackupResult> ShutdownAsync() {
		await _service.StopPreparingAsync();
		Timer? timer;
		lock (_gate) {
			timer = _backupTimer;
			_backupTimer = null;
		}
		if (timer != null) {
			await timer.DisposeAsync();
		}
		if (_shutdown) {
			return _finalBackup ?? new BackupResult(0, 0, [], true);
		}
		await _service.MaintenanceGate.WaitAsync();
		try {
			if (_shutdown) {
				return _finalBackup ?? new BackupResult(0, 0, [], true);
			}
			foreach (var id in Ids()) {
				await CheckpointAsync(id);
			}
			_finalBackup = BackupAllCore();
			_shutdown = true;
			return _finalBackup;
		} finally {
			_service.MaintenanceGate.Release();
		}
	}

	internal static void SetDataDirectoryForTest(string? path) {
		_service.ResetForTest(path);
		lock (_gate) {
			_known.Clear();
			_shutdown = false;
			_finalBackup = null;
			_lastBackupAttempts.Clear();
			_lastBackupOutcomes.Clear();
			_retentionWarning = null;
			var timer = _backupTimer;
			_backupTimer = null;
			timer?.Dispose();
		}
	}

	internal static string DatabasePathForTest(string id) {
		return Path.Combine(_service.DataDirectory, DatabaseIdentity.FileName(id));
	}

	internal static void ResetPrepareStateForTest() {
		_service.ResetPrepareState();
	}

	private static List<string> Ids() {
		lock (_gate) {
			var list = new List<string>(_known);
			list.Sort(StringComparer.Ordinal);
			return list;
		}
	}

	private static DatabaseStatus BuildStatus(string id) {
		var path = Path.Combine(_service.DataDirectory, DatabaseIdentity.FileName(id));
		var exists = File.Exists(path);
		var rolling = DatabaseBackupStore.ListCompleted(_service.DataDirectory, id, DatabaseBackupStore.RollingCategory);
		var preMigration = DatabaseBackupStore.ListCompleted(_service.DataDirectory, id, DatabaseBackupStore.PreMigrationCategory);
		IReadOnlyList<MaintenanceOperationRecord> pending;
		try {
			pending = PendingMaintenance(id);
		} catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) {
			pending = [new MaintenanceOperationRecord { OperationId = "invalid", Database = id, Category = "maintenance", State = "Invalid", StartedUtc = DateTimeOffset.UtcNow, Reason = e.Message }];
		}
		var notes = new List<string>();
		if (!exists) notes.Add("missing");
		long size = 0;
		long walSize = 0;
		string? history = null;
		if (exists) {
			size = new FileInfo(path).Length;
			var wal = path + "-wal";
			if (File.Exists(wal)) walSize = new FileInfo(wal).Length;
			try {
				var applied = pending.Count == 0 ? DatabaseBackupStore.ReadHistory(path) : [];
				history = applied.LastOrDefault();
				if (applied.Count == 0) notes.Add("no-history");
			} catch (SqliteException e) {
				notes.Add(e.Message);
			}
		}
		var state = _service.StateOf(id);
		if (pending.Count > 0 && state != DatabaseState.Preparing) state = DatabaseState.MaintenanceRequired;
		if (!exists && state == DatabaseState.Unprepared) state = DatabaseState.NotPresent;
		DateTimeOffset? attempt;
		bool? succeeded;
		lock (_gate) {
			_lastBackupAttempts.TryGetValue(id, out attempt);
			succeeded = _lastBackupOutcomes.TryGetValue(id, out var outcome) ? outcome : null;
			if (LastRetentionWarning != null) notes.Add(LastRetentionWarning);
		}
		var last = rolling.Concat(preMigration).OrderByDescending(info => info.CreatedUtc).FirstOrDefault();
		return new DatabaseStatus(id, state, size, walSize, history, exists ? path : null, last, attempt, succeeded, pending.Count > 0 ? pending[0] : null, rolling.Count + preMigration.Count, notes);
	}

	private static void OnBackupTimer(object? state) {
		if (!_service.MaintenanceGate.Wait(0)) {
			Logger.Info(typeof(Databases), Loc.Get("BackupSkippedBusy"));
			return;
		}
		try {
			BackupAllCore();
		} catch (Exception e) {
			Logger.Error(typeof(Databases), e, Loc.Get("BackupTimerFailed"));
		} finally {
			_service.MaintenanceGate.Release();
		}
	}

	private static BackupResult BackupAllCore() {
		var list = Ids();
		var failures = new List<BackupFailure>();
		var succeeded = 0;
		lock (_gate) _retentionWarning = null;
		foreach (var id in list) {
			lock (_gate) _lastBackupAttempts[id] = DateTimeOffset.UtcNow;
			if (BackupOne(id, out var reason)) {
				succeeded++;
				lock (_gate) _lastBackupOutcomes[id] = true;
			} else {
				lock (_gate) _lastBackupOutcomes[id] = false;
				failures.Add(new BackupFailure(DatabaseIdentity.FileName(id), reason ?? Loc.Get("BackupFailedSeeLog")));
			}
		}
		return new BackupResult(list.Count, succeeded, failures, RetentionWarning: LastRetentionWarning != null);
	}

	private static bool BackupOne(string id, out string? failure) {
		failure = null;
		if (MaintenanceBlocked(id, out failure)) return false;
		var path = Path.Combine(_service.DataDirectory, DatabaseIdentity.FileName(id));
		if (!File.Exists(path)) {
			failure = Loc.Format("BackupSourceMissing", id);
			return false;
		}
		if (!DatabaseBackupStore.TryCreate(_service.DataDirectory, id, DatabaseBackupStore.RollingCategory, path, [], out _, out failure)) {
			return false;
		}
		if (!DatabaseBackupStore.TryPruneRolling(_service.DataDirectory, id, HostMaintenance.ProtectedRestoreBackupId(id), out var pruneFailure)) {
			lock (_gate) _retentionWarning = pruneFailure;
		}
		return true;
	}

	private static string? _retentionWarning;

	public static string? LastRetentionWarning {
		get {
			lock (_gate) return _retentionWarning;
		}
	}

	private static bool MaintenanceBlocked(string id, out string? reason) {
		reason = null;
		try {
			if (MaintenanceJournal.ListUnfinishedRestores(DataDirectory, id).Count == 0 && MaintenanceJournal.ReadMigrationIntent(DataDirectory, id) == null) return false;
			reason = Loc.Format("DatabaseMaintenanceRequired", id);
		} catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) {
			reason = e.Message;
		}
		return true;
	}

	private static async Task CheckpointAsync(string id) {
		if (MaintenanceBlocked(id, out _)) return;
		var path = Path.Combine(_service.DataDirectory, DatabaseIdentity.FileName(id));
		if (!File.Exists(path)) return;
		try {
			using var connection = SqliteConnectionPolicy.OpenExisting(path);
			using var command = connection.CreateCommand();
			command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
			var result = await command.ExecuteScalarAsync();
			if (result is not (0 or 0L)) {
				Logger.Error(typeof(Databases), Loc.Format("CheckpointBusy", DatabaseIdentity.FileName(id), result));
			}
		} catch (Exception e) {
			Logger.Error(typeof(Databases), e, Loc.Format("CheckpointFailed", DatabaseIdentity.FileName(id)));
		}
	}
}

public sealed record BackupResult(int Total, int Succeeded, IReadOnlyList<BackupFailure> Failures, bool RetentionWarning = false) {
	public bool Success => Failures.Count == 0 && Succeeded == Total;
}

public sealed record BackupFailure(string File, string Reason);
