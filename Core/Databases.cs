using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Npgsql;
using Pengin1011.Core.Logging;

namespace Pengin1011.Core;

public static class Databases {
	public const int BackupIntervalMinutes = 30;

	private static readonly DatabaseService _service = new();
	private static readonly object _gate = new();
	private static readonly HashSet<string> _discovered = new(StringComparer.OrdinalIgnoreCase);

	private static Timer? _backupTimer;
	private static bool _shutdown;
	private static BackupResult? _finalBackup;
	private static readonly Dictionary<string, DateTimeOffset?> _lastBackupAttempts = new(StringComparer.OrdinalIgnoreCase);
	private static readonly Dictionary<string, bool> _lastBackupOutcomes = new(StringComparer.OrdinalIgnoreCase);

	public static string DataDirectory => _service.DataDirectory;

	public static bool DataDirectoryConfigured => _service.DataDirectoryConfigured;
	public static DatabaseProvider Provider => _service.Provider;
	internal static void ConfigureProvider(DatabaseConfig config) => _service.ConfigureProvider(config);

	internal static async Task<string> ExternalVersionAsync() {
		using var timeout = new CancellationTokenSource(DatabaseService.PrepareBudget);
		try {
			await using DbConnection connection = Provider == DatabaseProvider.MySQL
				? new MySqlConnection(_service.ExternalConnectionString) : new NpgsqlConnection(_service.ExternalConnectionString);
			await connection.OpenAsync(timeout.Token);
			return Loc.Format("ExternalDatabaseVersion", Provider, connection.ServerVersion, connection.GetType().Assembly.GetName().Version);
		} catch (Exception e) {
			var failure = DatabaseOptions.Failure(e, timedOut: timeout.IsCancellationRequested);
			Logger.Error(typeof(Databases), failure);
			return failure;
		}
	}

	internal static SemaphoreSlim BackupGate => _service.MaintenanceGate;

	internal static void ConfigureDataDirectory(string dataDirectory) {
		_service.Configure(dataDirectory);
	}

	internal static bool TryResolveDataDirectory(DatabasePathOptions options, out string directory, out string? failure) {
		DataDirectoryResolver.Resolve(options, out directory, out failure);
		return failure == null;
	}

	internal static void ReleaseOwnership() => _service.ReleaseOwnership();
	internal static void AcquireOwnership() => _service.AcquireOwnership();

	internal static void Init() {
		if (Provider != DatabaseProvider.SQLite) return;
		if (!_service.DataDirectoryConfigured) _service.Configure(DataDirectoryResolver.Default);
		_service.AcquireOwnership();
		var discovered = Directory.EnumerateFiles(_service.DataDirectory, "*.db", SearchOption.TopDirectoryOnly)
			.Concat(Directory.EnumerateFiles(_service.DataDirectory, "*.pending", SearchOption.TopDirectoryOnly))
			.Select(path => Path.GetFileNameWithoutExtension(path)).ToArray();
		lock (_gate) {
			foreach (var id in discovered) _discovered.Add(id);
			_backupTimer ??= new Timer(OnBackupTimer, null, TimeSpan.FromMinutes(BackupIntervalMinutes), TimeSpan.FromMinutes(BackupIntervalMinutes));
		}
	}

	internal static bool RegisterModule(Assembly assembly) {
		var attribute = assembly.GetCustomAttribute<ModuleDatabaseProviderAttribute>();
		if (attribute == null) return false;
		var provider = (IModuleDatabaseProvider)Activator.CreateInstance(attribute.ProviderType)!;
		var registrar = new DatabaseRegistrar(Provider);
		provider.Register(registrar);
		return _service.Register(assembly, registrar.Definitions);
	}

	internal static void UnregisterModule(Assembly assembly) => _service.Unregister(assembly);

	internal static Task PrepareModuleAsync(Assembly assembly, CancellationToken ct) => _service.PrepareModuleAsync(assembly, ct);

	internal static void RegisterForTest(DatabaseRegistrar registrar) => _service.Register(typeof(Databases).Assembly, registrar.Definitions);

	internal static async Task PrepareAsync<TContext>(CancellationToken ct = default) where TContext : DbContext {
		var result = await _service.PrepareAsync<TContext>(ct);
		ct.ThrowIfCancellationRequested();
		if (!result.Success) throw new InvalidOperationException(result.Failure);
	}

	public static DatabaseState StateOf<TContext>() where TContext : DbContext => _service.StateOf<TContext>();

	public static TContext Open<TContext>() where TContext : DbContext {
		var entry = _service.EntryOf(typeof(TContext));
		if (entry.State != DatabaseState.Prepared || Provider == DatabaseProvider.SQLite && DatabaseMaintenance.Exists(DataDirectory, entry.Id)) {
			throw new InvalidOperationException(Loc.Format("DatabasePrepareRequired", entry.Id));
		}
		_service.AcquireOwnership();
		return (TContext)entry.Create(_service.ConnectionStringFor(entry, creating: false));
	}

	public static async Task<BackupResult> BackupNowAsync(CancellationToken ct = default) {
		if (Provider != DatabaseProvider.SQLite) return new BackupResult(0, 0, [new BackupFailure("", Loc.Get("ExternalDatabaseBackupUnsupported"))]);
		await _service.MaintenanceGate.WaitAsync(ct);
		try {
			_service.AcquireOwnership();
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
		if (Provider != DatabaseProvider.SQLite) throw new NotSupportedException(Loc.Get("ExternalDatabaseBackupUnsupported"));
		return DatabaseBackupStore.ListCompleted(_service.DataDirectory, id, category);
	}

	internal static async Task<BackupResult> ShutdownAsync() {
		await _service.StopPreparingAsync();
		if (Provider != DatabaseProvider.SQLite) return new BackupResult(0, 0, []);
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
			foreach (var id in Ids(forBackup: true)) {
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
			_discovered.Clear();
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

	private static List<string> Ids(bool forBackup = false) {
		if (Provider != DatabaseProvider.SQLite) return forBackup ? [] : _service.Ids().OrderBy(id => id, StringComparer.Ordinal).ToList();
		HashSet<string> discovered;
		lock (_gate) discovered = new HashSet<string>(_discovered, StringComparer.OrdinalIgnoreCase);
		var ids = new HashSet<string>(_service.Ids(), StringComparer.OrdinalIgnoreCase);
		ids.UnionWith(discovered);
		return ids.Where(id => !forBackup || discovered.Contains(id) || File.Exists(_service.PathOf(id))
			|| _service.StateOf(id) == DatabaseState.Prepared || DatabaseMaintenance.Exists(DataDirectory, id))
			.OrderBy(id => id, StringComparer.Ordinal).ToList();
	}

	private static DatabaseStatus BuildStatus(string id) {
		if (Provider != DatabaseProvider.SQLite) {
			var entry = _service.FindEntry(id);
			return new DatabaseStatus(id, entry?.State ?? DatabaseState.NotPresent, null, null, entry?.CurrentHistory, null,
				null, null, null, false, 0, entry?.Failure is { } failure ? [failure] : [], Provider);
		}
		var path = Path.Combine(_service.DataDirectory, DatabaseIdentity.FileName(id));
		var exists = File.Exists(path);
		var rolling = DatabaseBackupStore.ListCompleted(_service.DataDirectory, id, DatabaseBackupStore.RollingCategory);
		var preMigration = DatabaseBackupStore.ListCompleted(_service.DataDirectory, id, DatabaseBackupStore.PreMigrationCategory);
		var pending = DatabaseMaintenance.Exists(DataDirectory, id);
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
				var applied = !pending ? DatabaseBackupStore.ReadHistory(path) : [];
				history = applied.LastOrDefault();
				if (applied.Count == 0) notes.Add("no-history");
			} catch (SqliteException e) {
				notes.Add(e.Message);
			}
		}
		var state = _service.StateOf(id);
		if (pending && state != DatabaseState.Preparing) state = DatabaseState.MaintenanceRequired;
		if (!exists && state == DatabaseState.Unprepared) state = DatabaseState.NotPresent;
		DateTimeOffset? attempt;
		bool? succeeded;
		lock (_gate) {
			_lastBackupAttempts.TryGetValue(id, out attempt);
			succeeded = _lastBackupOutcomes.TryGetValue(id, out var outcome) ? outcome : null;
			if (LastRetentionWarning != null) notes.Add(LastRetentionWarning);
		}
		var last = rolling.Concat(preMigration).OrderByDescending(info => info.CreatedUtc).FirstOrDefault();
		return new DatabaseStatus(id, state, size, walSize, history, exists ? path : null, last, attempt, succeeded, pending, rolling.Count + preMigration.Count, notes);
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
		var list = Ids(forBackup: true);
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
		if (!DatabaseBackupStore.TryPruneRolling(_service.DataDirectory, id, out var pruneFailure)) {
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
		reason = DatabaseMaintenance.Exists(DataDirectory, id) ? Loc.Format("DatabaseMaintenanceRequired", id) : null;
		return reason != null;
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
