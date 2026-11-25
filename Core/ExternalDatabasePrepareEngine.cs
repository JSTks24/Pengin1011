using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pengin1011.Core.Logging;

namespace Pengin1011.Core;

internal static class ExternalDatabasePrepareEngine {
	internal static async Task<DatabasePrepareResult> RunAsync(DatabaseService service, DatabaseService.DatabaseEntry entry, CancellationToken ct) {
		await service.MaintenanceGate.WaitAsync(ct).ConfigureAwait(false);
		try {
			await using var context = entry.Create(service.ExternalConnectionString);
			var migrations = context.Database.GetMigrations().ToArray();
			if (migrations.Length == 0) return Failed(Loc.Format("DbMigrationContractViolated", entry.ContextType.Name, Loc.Get("MigrationContractText")));
			if (context.GetService<IMigrationsAssembly>().ModelSnapshot == null) return Failed(Loc.Format("DatabaseSnapshotMissing", entry.Id));
			if (context.Database.HasPendingModelChanges()) return Failed(Loc.Format("DatabaseModelDrift", entry.Id));
			await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
			var history = (await context.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToArray();
			entry.CurrentHistory = history.LastOrDefault();
			if (!DatabasePrepareEngine.IsLegalPrefix(history, migrations)) return Failed(Loc.Format("DatabaseHistoryNotPrefix", entry.Id, string.Join(",", history)));
			if (history.Length == 0 && await HasUserTablesAsync(context, service.Provider, entry.Id, ct).ConfigureAwait(false)) {
				return Failed(Loc.Format("DatabaseUntrackedTables", entry.Id));
			}
			ct.ThrowIfCancellationRequested();
			await context.Database.MigrateAsync(ct).ConfigureAwait(false);
			var applied = (await context.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToArray();
			if (!applied.SequenceEqual(migrations, StringComparer.Ordinal)) return Failed(Loc.Format("DatabaseMigrationIncomplete", entry.Id, string.Join(",", applied)));
			entry.CurrentHistory = applied.LastOrDefault();
			return new DatabasePrepareResult(true, DatabaseState.Prepared, null);
		} finally {
			service.MaintenanceGate.Release();
		}
	}

	private static DatabasePrepareResult Failed(string message) {
		Logger.Error(typeof(ExternalDatabasePrepareEngine), message);
		return new DatabasePrepareResult(false, DatabaseState.Failed, message);
	}

	internal static async Task<bool> HasUserTablesAsync(DbContext context, DatabaseProvider provider, string id, CancellationToken ct) {
		await using var command = context.Database.GetDbConnection().CreateCommand();
		command.CommandText = provider == DatabaseProvider.MySQL
			? "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE()"
			: "SELECT tablename FROM pg_catalog.pg_tables WHERE schemaname = 'public'";
		command.CommandTimeout = 30;
		await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
		var prefix = DatabaseOptions.Prefix(id);
		var history = DatabaseOptions.HistoryTable(id);
		while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
			var name = reader.GetString(0);
			if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !name.Equals(history, StringComparison.OrdinalIgnoreCase)) return true;
		}
		return false;
	}
}
