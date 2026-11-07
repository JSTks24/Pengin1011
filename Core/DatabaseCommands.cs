using Microsoft.Data.Sqlite;
using Pengin1011.Core.Logging;

namespace Pengin1011.Core;

public static class DatabaseCommands {
	internal static async Task<int> RunOfflineAsync(IReadOnlyList<string> args) {
		if (args.Count != 7 || args[0] != "db" || args[1] != "restore" || args[4] != "--data-dir" || args[6] != "--confirm"
			|| string.IsNullOrWhiteSpace(args[5]) || !DatabaseIdentity.IsSafeIdentifier(args[2]) || !DatabaseIdentity.IsSafeIdentifier(args[3])) {
			Console.WriteLine(Loc.Get("DbOfflineUsage"));
			return 2;
		}
		try {
			if (!Databases.TryResolveDataDirectory(new DatabasePathOptions(args[5], null, false), out var directory, out var failure)) {
				Console.WriteLine(Loc.Format("DataDirectoryMissing", failure));
				return 1;
			}
			if (!DataDirectoryOwnership.TryAcquire(directory, out var ownership, out failure)) {
				Console.WriteLine(Loc.Format("DataDirectoryNotOwned", directory, failure));
				return 1;
			}
			using (ownership) {
				var result = await DatabaseRestoreEngine.RestoreAsync(directory, args[2], args[3], CancellationToken.None);
				Console.WriteLine(result.Outcome == RestoreOutcome.Completed ? Loc.Get("RestoreOfflineCompleted") : Loc.Format("DbCommandRejected", result.Reason));
				return result.Outcome == RestoreOutcome.Completed ? 0 : 1;
			}
		} catch (Exception e) {
			Logger.Error(typeof(DatabaseCommands), e, Loc.Get("RestoreOfflineFailed"));
			return 1;
		}
	}

	public static async Task<string> ExecuteAsync(IReadOnlyList<string> arguments) {
		if (arguments.Count < 2 || arguments[0] != "db") return Loc.Get("DbInternalUsage");
		var name = arguments[1].ToLowerInvariant();
		var count = arguments.Count - 2;
		if (name is not ("version" or "status" or "backups" or "backup" or "verify")
			|| name is "version" or "backup" && count != 0 || name == "status" && count > 1
			|| name == "backups" && count != 1 || name == "verify" && count != 2
			|| arguments.Skip(2).Any(value => !DatabaseIdentity.IsSafeIdentifier(value) || value.StartsWith("--", StringComparison.Ordinal))) return Loc.Get("DbInternalUsage");
		if (name != "version" && !Databases.DataDirectoryConfigured) return Loc.Get("ConfigPathsUnavailable");
		if (name == "backup") {
			var result = await Databases.BackupNowAsync();
			return result.Success ? Loc.Format("DbBackupCompleted", result.Succeeded, result.Total)
				: Loc.Format("DbBackupPartial", result.Succeeded, result.Total, string.Join("\n", result.Failures.Select(failure => failure.Reason)));
		}
		await Databases.BackupGate.WaitAsync();
		try {
			return ExecuteCore(name, count > 0 ? arguments[2] : null, count > 1 ? arguments[3] : null);
		} finally {
			Databases.BackupGate.Release();
		}
	}

	private static string ExecuteCore(string name, string? id, string? value) {
		switch (name) {
			case "version":
				using (var connection = new SqliteConnection("Data Source=:memory:")) {
					connection.Open();
					using var version = connection.CreateCommand();
					version.CommandText = "SELECT sqlite_version()";
					return Loc.Format("DatabaseVersionReport", version.ExecuteScalar(), typeof(SqliteConnection).Assembly.GetName().Version,
						SqliteConnectionPolicy.ReadJournalMode(connection), SqliteConnectionPolicy.ReadSynchronous(connection), SqliteConnectionPolicy.ReadForeignKeys(connection));
				}
			case "status":
				var statuses = id == null ? Databases.GetStatus() : [Databases.GetStatus(id)];
				return statuses.Count == 0 ? Loc.Get("NoDatabases") : string.Join("\n", statuses.Select(status =>
					Loc.Format("DbInspectHeader", status.Database, status.State, status.SizeBytes, status.WalSizeBytes, status.CurrentHistory ?? "-",
						status.CompletedBackupCount, status.PendingMaintenance ? "pending" : "-") + string.Join("", status.Notes.Select(note => "\n" + note))));
			case "backups":
				var entries = new[] { DatabaseBackupStore.RollingCategory, DatabaseBackupStore.PreMigrationCategory }
					.SelectMany(category => Databases.ListBackups(id!, category))
					.Select(backup => Loc.Format("DbBackupsHeader", backup.Category, backup.BackupId, backup.CreatedUtc.ToString("O"), backup.SizeBytes));
				return string.Join("\n", entries);
			case "verify":
				foreach (var category in new[] { DatabaseBackupStore.RollingCategory, DatabaseBackupStore.PreMigrationCategory }) {
					if (DatabaseBackupStore.TryVerify(Databases.DataDirectory, id!, category, value!, out _, out _)) return Loc.Format("DbCommandCompleted", value);
				}
				return Loc.Format("RestoreBackupInvalid", value, "");
			default:
				return Loc.Get("DbInternalUsage");
		}
	}
}
