using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using QingQiu1011.Core.Logging;

namespace QingQiu1011.Core;

public static class Databases {
	private const int BackupIntervalMinutes = 30;
	private const int BackupKeepCount = 5;
	private const string MigrationContract = "凡有持久化需求的模块一律使用 EF Core Migration";

	private static readonly object Gate = new();
	internal static readonly SemaphoreSlim BackupGate = new(1, 1);
	private static readonly ConcurrentDictionary<string, byte> KnownFiles = new(StringComparer.Ordinal);
	private static readonly ConcurrentDictionary<string, Type> FileOwners = new(StringComparer.Ordinal);
	private static readonly ConcurrentDictionary<string, byte> WalApplied = new(StringComparer.Ordinal);
	private static Timer? _backupTimer;
	private static int _backupSeq;
	private static string? _dataDirectoryOverride;
	private static bool _shutdown;
	private static BackupResult? _finalBackup;

	private static string DataDirectory => Path.Combine(_dataDirectoryOverride ?? AppContext.BaseDirectory, "data");

	private static string BackupDirectory => Path.Combine(DataDirectory, "backup");

	public static void Init() {
		lock (Gate) {
			Directory.CreateDirectory(DataDirectory);
			foreach (var file in Directory.EnumerateFiles(DataDirectory, "*.db", SearchOption.TopDirectoryOnly)) {
				if (file.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) KnownFiles.TryAdd(Path.GetFileName(file), 0);
			}
			_backupTimer ??= new Timer(OnBackupTimer, null, TimeSpan.FromMinutes(BackupIntervalMinutes), TimeSpan.FromMinutes(BackupIntervalMinutes));
		}
	}

	public static TContext Open<TContext>() where TContext : DbContext {
		var context = Activator.CreateInstance<TContext>();
		var file = $"{ModuleName(typeof(TContext))}.db";
		var owner = FileOwners.GetOrAdd(file, typeof(TContext));
		if (owner != typeof(TContext)) {
			context.Dispose();
			var message = $"库文件冲突：{file} 已被 {owner.Name} 占用，{typeof(TContext).Name} 无法使用同一文件";
			Logger.Error(typeof(Databases), message);
			throw new InvalidOperationException(message);
		}
		Directory.CreateDirectory(DataDirectory);
		KnownFiles.TryAdd(file, 0);
		if (!context.Database.GetMigrations().Any()) {
			context.Dispose();
			var message = $"数据库契约违约：{typeof(TContext).Name} 没有任何 Migration（{MigrationContract}）";
			Logger.Error(typeof(Databases), message);
			throw new InvalidOperationException(message);
		}
		context.Database.Migrate();
		if (WalApplied.TryAdd(file, 0)) {
			context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL");
		}
		return context;
	}

	public static void Configure(Type contextType, DbContextOptionsBuilder options) {
		options.UseSqlite($"Data Source={Path.Combine(DataDirectory, $"{ModuleName(contextType)}.db")}");
	}

	public static async Task<BackupResult> BackupNowAsync(CancellationToken ct = default) {
		await BackupGate.WaitAsync(ct);
		try {
			return BackupAllCore();
		} finally {
			BackupGate.Release();
		}
	}

	public static IReadOnlyList<DbStatus> GetStatus() {
		var list = new List<DbStatus>();
		foreach (var file in KnownFiles.Keys.OrderBy(file => file, StringComparer.Ordinal)) {
			var path = Path.Combine(DataDirectory, file);
			if (!File.Exists(path)) continue;
			var moduleName = Path.GetFileNameWithoutExtension(file);
			list.Add(new DbStatus(moduleName, new FileInfo(path).Length, ReadMigrationId(path), ReadLastBackup(moduleName)));
		}
		return list;
	}

	public static async Task<BackupResult> ShutdownAsync() {
		Timer? timer;
		lock (Gate) {
			timer = _backupTimer;
			_backupTimer = null;
		}
		if (timer != null) {
			await timer.DisposeAsync();
		}
		if (_shutdown) {
			return _finalBackup ?? new BackupResult(0, 0, []);
		}
		await BackupGate.WaitAsync();
		try {
			if (_shutdown) {
				return _finalBackup ?? new BackupResult(0, 0, []);
			}
			foreach (var file in KnownFiles.Keys) {
				await CheckpointAsync(file);
			}
			_finalBackup = BackupAllCore();
			_shutdown = true;
			return _finalBackup;
		} finally {
			BackupGate.Release();
		}
	}

	internal static void SetDataDirectoryForTest(string? path) {
		lock (Gate) {
			_dataDirectoryOverride = path;
			FileOwners.Clear();
			WalApplied.Clear();
			KnownFiles.Clear();
			_shutdown = false;
			_finalBackup = null;
		}
	}

	private static string? ReadMigrationId(string path) {
		try {
			using var context = new MaintenanceContext(new DbContextOptionsBuilder().UseSqlite($"Data Source={path}").Options);
			return context.Database.GetAppliedMigrations()
				.OrderByDescending(migration => migration, StringComparer.Ordinal)
				.FirstOrDefault();
		} catch (Exception e) {
			Logger.Error(typeof(Databases), e, $"读取迁移版本失败：{Path.GetFileName(path)}");
			return null;
		}
	}

	private static DateTimeOffset? ReadLastBackup(string moduleName) {
		if (!Directory.Exists(BackupDirectory)) return null;
		var latest = Directory.EnumerateFiles(BackupDirectory, $"{moduleName}-*.db")
			.OrderByDescending(file => file, StringComparer.Ordinal)
			.FirstOrDefault();
		return latest == null ? null : new FileInfo(latest).LastWriteTime;
	}

	private static void OnBackupTimer(object? state) {
		if (!BackupGate.Wait(0)) {
			Logger.Info(typeof(Databases), "上一轮备份仍在进行，本轮定时备份跳过");
			return;
		}
		try {
			BackupAllCore();
		} finally {
			BackupGate.Release();
		}
	}

	private static string ModuleName(Type contextType) {
		var name = contextType.Name;
		if (name.EndsWith("DbContext", StringComparison.Ordinal)) {
			name = name[..^9];
		} else if (name.EndsWith("Context", StringComparison.Ordinal)) {
			name = name[..^7];
		}
		if (name.Length == 0) {
			var message = $"DbContext 类型名 {contextType.Name} 无法推导模块名";
			Logger.Error(typeof(Databases), message);
			throw new ArgumentException(message, nameof(contextType));
		}
		return name;
	}

	private static BackupResult BackupAllCore() {
		var files = KnownFiles.Keys.OrderBy(file => file, StringComparer.Ordinal).ToList();
		var failures = new List<BackupFailure>();
		var succeeded = 0;
		foreach (var file in files) {
			if (BackupOne(file)) succeeded++;
			else failures.Add(new BackupFailure(file, "备份失败，详见错误日志"));
		}
		return new BackupResult(files.Count, succeeded, failures);
	}

	private static bool BackupOne(string file) {
		var path = Path.Combine(DataDirectory, file);
		if (!File.Exists(path)) {
			return false;
		}
		try {
			Directory.CreateDirectory(BackupDirectory);
			var seq = Interlocked.Increment(ref _backupSeq);
			var target = Path.Combine(BackupDirectory, $"{Path.GetFileNameWithoutExtension(file)}-{DateTime.Now:yyyyMMdd-HHmmssfff}-{seq:000}.db");
			using var context = new MaintenanceContext(new DbContextOptionsBuilder().UseSqlite($"Data Source={path}").Options);
			context.Database.ExecuteSql($"VACUUM INTO {target}");
			PruneBackups(Path.GetFileNameWithoutExtension(file));
			return true;
		} catch (Exception e) {
			Logger.Error(typeof(Databases), e, $"备份失败：{file}");
			return false;
		}
	}

	private static void PruneBackups(string baseName) {
		var outdated = Directory.EnumerateFiles(BackupDirectory, $"{baseName}-*.db")
			.OrderByDescending(file => file, StringComparer.Ordinal)
			.Skip(BackupKeepCount);
		foreach (var file in outdated) {
			File.Delete(file);
		}
	}

	private static async Task CheckpointAsync(string file) {
		var path = Path.Combine(DataDirectory, file);
		if (!File.Exists(path)) {
			return;
		}
		try {
			using var context = new MaintenanceContext(new DbContextOptionsBuilder().UseSqlite($"Data Source={path}").Options);
			var connection = context.Database.GetDbConnection();
			if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
			await using var command = connection.CreateCommand();
			command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
			var result = await command.ExecuteScalarAsync();
			if (result is not (0 or 0L)) {
				Logger.Error(typeof(Databases), $"退出 checkpoint 报告忙碌：{file}（busy={result}）");
			}
		} catch (Exception e) {
			Logger.Error(typeof(Databases), e, $"退出保存失败：{file}");
		}
	}

	private sealed class MaintenanceContext : DbContext {
		public MaintenanceContext(DbContextOptions options) : base(options) { }
	}
}

public sealed record DbStatus(string Module, long SizeBytes, string? MigrationId, DateTimeOffset? LastBackup);

public sealed record BackupResult(int Total, int Succeeded, IReadOnlyList<BackupFailure> Failures) {
	public bool Success => Failures.Count == 0 && Succeeded == Total;
}

public sealed record BackupFailure(string File, string Reason);
