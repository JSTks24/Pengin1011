using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;

namespace Pengin1011.Core;

internal static class DatabasePrepareEngine {
	internal static Func<CancellationToken, Task>? IntentWrittenForTest;
	public static async Task<DatabasePrepareResult> RunAsync(DatabaseService service, string id, Type contextType, CancellationToken ct) {
		var path = service.PathOf(id);
		var directory = service.DataDirectory;
		Directory.CreateDirectory(directory);
		Directory.CreateDirectory(Path.Combine(directory, "backup", id));
		var existed = File.Exists(path);

		await service.MaintenanceGate.WaitAsync(ct).ConfigureAwait(false);
		try {
			return await RunUnderMaintenanceAsync(service, id, contextType, path, directory, existed, ct).ConfigureAwait(false);
		} finally {
			service.MaintenanceGate.Release();
		}
	}

	private static async Task<DatabasePrepareResult> RunUnderMaintenanceAsync(DatabaseService service, string id, Type contextType, string path, string directory, bool existed, CancellationToken ct) {
		var factory = DatabaseContextFactory.For(contextType);
		try {
			using var probe = factory(service.ConnectionStringFor(contextType, creating: !existed));
			var migrations = probe.Database.GetMigrations().ToList();
			if (migrations.Count == 0) {
				var message = Loc.Format("DbMigrationContractViolated", contextType.Name, Loc.Get("MigrationContractText"));
				Logger.Error(typeof(DatabasePrepareEngine), message);
				return new DatabasePrepareResult(false, DatabaseState.Failed, message);
			}
			var snapshot = probe.GetService<IMigrationsAssembly>().ModelSnapshot;
			if (snapshot == null) {
				var message = Loc.Format("DatabaseSnapshotMissing", id);
				Logger.Error(typeof(DatabasePrepareEngine), message);
				return new DatabasePrepareResult(false, DatabaseState.Failed, message);
			}
			var pendingModelChanges = probe.Database.HasPendingModelChanges();
			if (pendingModelChanges) {
				var message = Loc.Format("DatabaseModelDrift", id);
				Logger.Error(typeof(DatabasePrepareEngine), message);
				return new DatabasePrepareResult(false, DatabaseState.Failed, message);
			}

			var history = existed && File.Exists(path) ? DatabaseBackupStore.ReadHistory(path) : [];
			if (existed && !IsLegalPrefix(history, migrations)) {
				var message = Loc.Format("DatabaseHistoryNotPrefix", id, string.Join(",", history));
				Logger.Error(typeof(DatabasePrepareEngine), message);
				return new DatabasePrepareResult(false, DatabaseState.Failed, message);
			}
			if (existed && history.Count == 0 && HasUserTables(path)) {
				var message = Loc.Format("DatabaseUntrackedTables", id);
				Logger.Error(typeof(DatabasePrepareEngine), message);
				return new DatabasePrepareResult(false, DatabaseState.Failed, message);
			}
			if (HasResidualMigrationLock(path)) {
				var message = Loc.Format("DatabaseResidualMigrationLock", id);
				Logger.Error(typeof(DatabasePrepareEngine), message);
				return new DatabasePrepareResult(false, DatabaseState.Failed, message);
			}

			var pending = migrations.Except(history, StringComparer.Ordinal).ToList();
			if (pending.Count > 0) {
				string? snapshotId = null;
				if (existed && !DatabaseBackupStore.TryCreate(directory, id, DatabaseBackupStore.PreMigrationCategory, path, pending, out snapshotId, out var snapshotFailure)) {
					var message = Loc.Format("PreMigrationSnapshotFailed", id, snapshotFailure);
					Logger.Error(typeof(DatabasePrepareEngine), message);
					return new DatabasePrepareResult(false, DatabaseState.Failed, message);
				}
				var record = new MaintenanceJournal.MigrationIntent {
					Database = id,
					OperationId = MaintenanceJournal.CreateOperationId(),
					State = nameof(MaintenanceOperationState.Migrating),
					PreviousHistory = history,
					TargetHistory = migrations,
					SnapshotId = snapshotId,
					FreshDatabase = !existed,
					UpdatedUtc = DateTimeOffset.UtcNow,
				};
				MaintenanceJournal.WriteMigrationIntent(directory, id, record);
				if (IntentWrittenForTest != null) await IntentWrittenForTest(ct);
				using var migrating = factory(service.ConnectionStringFor(contextType, creating: !existed));
				await migrating.Database.MigrateAsync(ct).ConfigureAwait(false);
			}

			using (var verify = factory(SqliteConnectionPolicy.ForExistingDatabase(path))) {
				var applied = verify.Database.GetAppliedMigrations().ToList();
				if (!IsLegalPrefix(applied, migrations) || applied.Count != migrations.Count) {
					var message = Loc.Format("DatabaseMigrationIncomplete", id, string.Join(",", applied));
					Logger.Error(typeof(DatabasePrepareEngine), message);
					return new DatabasePrepareResult(false, DatabaseState.Failed, message);
				}
			}

			using (var connection = SqliteConnectionPolicy.OpenExisting(path)) {
				using var wal = connection.CreateCommand();
				wal.CommandText = "PRAGMA journal_mode=WAL";
				wal.ExecuteScalar();
				if (SqliteConnectionPolicy.ReadIntegrityCheck(connection) != 1) {
					var message = Loc.Format("DatabaseIntegrityFailed", id);
					Logger.Error(typeof(DatabasePrepareEngine), message);
					return new DatabasePrepareResult(false, DatabaseState.Failed, message);
				}
				if (SqliteConnectionPolicy.ReadForeignKeyCheck(connection) != 0) {
					var message = Loc.Format("DatabaseForeignKeyFailed", id);
					Logger.Error(typeof(DatabasePrepareEngine), message);
					return new DatabasePrepareResult(false, DatabaseState.Failed, message);
				}
			}

			MaintenanceJournal.ClearMigrationIntent(directory, id);
			return new DatabasePrepareResult(true, DatabaseState.Prepared, null);
		} catch (Exception e) {
			Logger.Error(typeof(DatabasePrepareEngine), e, Loc.Format("DatabasePrepareFailed", id));
			return new DatabasePrepareResult(false, DatabaseState.Failed, e.Message);
		}
	}

	internal static bool IsLegalPrefix(IReadOnlyList<string> applied, IReadOnlyList<string> known) {
		if (applied.Count > known.Count) return false;
		for (var i = 0; i < applied.Count; i++) {
			if (!string.Equals(applied[i], known[i], StringComparison.Ordinal)) return false;
		}
		return true;
	}

	internal static bool HasUserTables(string path) {
		try {
			using var connection = SqliteConnectionPolicy.OpenReadOnly(path);
			using var command = connection.CreateCommand();
			command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory'";
			return Convert.ToInt64(command.ExecuteScalar()) > 0;
		} catch (SqliteException e) {
			Logger.Error(typeof(DatabasePrepareEngine), e, Loc.Format("DatabasePrepareFailed", Path.GetFileName(path)));
			throw;
		}
	}

	internal static bool HasResidualMigrationLock(string path) {
		if (!File.Exists(path)) return false;
		try {
			using var connection = SqliteConnectionPolicy.OpenReadOnly(path);
			using var command = connection.CreateCommand();
			command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsLock'";
			if (command.ExecuteScalar() == null) return false;
			using var rows = connection.CreateCommand();
			rows.CommandText = "SELECT COUNT(*) FROM __EFMigrationsLock";
			return Convert.ToInt64(rows.ExecuteScalar()) > 0;
		} catch (SqliteException e) {
			Logger.Error(typeof(DatabasePrepareEngine), e, Loc.Format("DatabasePrepareFailed", Path.GetFileName(path)));
			throw;
		}
	}
}

internal static class DatabaseContextFactory {
	public static Func<string, DbContext> For(Type contextType) {
		var optionsType = typeof(DbContextOptions<>).MakeGenericType(contextType);
		var constructor = contextType.GetConstructor([optionsType]);
		return connectionString => {
			var builderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
			var builder = (DbContextOptionsBuilder)Activator.CreateInstance(builderType)!;
			UseSqlite(builder, connectionString);
			var options = builder.Options;
			if (constructor != null) return (DbContext)constructor.Invoke([options])!;
			var plain = contextType.GetConstructor(Type.EmptyTypes)
				?? throw new InvalidOperationException($"Context {contextType.Name} needs a DbContextOptions constructor or a parameterless one");
			var context = (DbContext)plain.Invoke(null)!;
			context.Database.SetConnectionString(connectionString);
			return context;
		};
	}

	private static void UseSqlite(DbContextOptionsBuilder builder, string connectionString) {
		SqliteDbContextOptionsBuilderExtensions.UseSqlite(builder, connectionString);
		builder.AddInterceptors(SqliteConnectionPolicy.Interceptor);
	}
}
