using Microsoft.Data.Sqlite;

namespace Pengin1011.Core;

internal sealed record DatabaseCommand(string Name, string? Database, string? Value, bool Confirm, bool AcceptInterrupted, string? Recovery);

public static class DatabaseCommands {
	public static async Task<string> ExecuteAsync(IReadOnlyList<string> arguments, bool allowFileImport = true) {
		if (!TryParse(arguments, allowFileImport, out var command)) return Loc.Get("DbInternalUsage");
		var id = command!.Database;
		if (HostMaintenance.Busy && command.Name is not ("version" or "status" or "backups")) return Loc.Get("MaintenanceBusy");
		if (command.Name != "version" && !Databases.DataDirectoryConfigured) return Loc.Get("ConfigPathsUnavailable");
		switch (command.Name) {
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
						status.CompletedBackupCount, status.PendingMaintenance?.State ?? "-") + string.Join("", status.Notes.Select(note => "\n" + note))));
			case "backups":
				var entries = new[] { DatabaseBackupStore.RollingCategory, DatabaseBackupStore.PreMigrationCategory }
					.SelectMany(category => Databases.ListBackups(id!, category))
					.Select(backup => Loc.Format("DbBackupsHeader", backup.Category, backup.BackupId, backup.CreatedUtc.ToString("O"), backup.SizeBytes));
				return string.Join("\n", entries.Concat(Databases.ListLegacyBackups(id!).Select(file => "legacy: " + file)));
			case "backup":
				var result = await Databases.BackupNowAsync();
				return result.Success ? Loc.Format("DbBackupCompleted", result.Succeeded, result.Total)
					: Loc.Format("DbBackupPartial", result.Succeeded, result.Total, string.Join("\n", result.Failures.Select(failure => failure.Reason)));
			case "verify" when command.Value != null:
				foreach (var category in new[] { DatabaseBackupStore.RollingCategory, DatabaseBackupStore.PreMigrationCategory }) {
					if (DatabaseBackupStore.TryVerify(Databases.DataDirectory, id!, category, command.Value, out _, out _)) return Loc.Format("DbCommandCompleted", command.Value);
				}
				return Loc.Format("RestoreBackupInvalid", command.Value, "");
			case "import-backup":
				if (!command.Confirm) return Loc.Get("DbCommandConfirmationRequired");
				return Databases.TryImportLegacyBackup(id!, command.Value!, out var backupId, out var failure)
					? Loc.Format("DbCommandCompleted", backupId) : Loc.Format("DbCommandRejected", failure);
			default:
				if (!command.Confirm) return Loc.Get("DbCommandConfirmationRequired");
				if (command.Name == "verify" && !command.AcceptInterrupted) return Loc.Get("VerifyRequiresConfirmation");
				return await HostMaintenance.RequestAsync(command);
		}
	}

	internal static bool TryParse(IReadOnlyList<string> arguments, bool allowFileImport, out DatabaseCommand? command) {
		command = null;
		if (arguments.Count < 2 || arguments[0] != "db") return false;
		var name = arguments[1].ToLowerInvariant();
		if (name is not ("status" or "backups" or "backup" or "version" or "verify" or "restore" or "recover" or "unlock" or "import-backup")) return false;
		if (name == "import-backup" && !allowFileImport) return false;
		var positional = new List<string>();
		var confirm = false;
		var acceptInterrupted = false;
		string? recovery = null;
		foreach (var token in arguments.Skip(2)) {
			switch (token) {
				case "--confirm": confirm = true; break;
				case "--accept-interrupted": acceptInterrupted = true; break;
				case "--complete":
				case "--rollback":
					if (recovery != null) return false;
					recovery = token;
					break;
				default:
					if (token.StartsWith("--", StringComparison.Ordinal)) return false;
					positional.Add(token);
					break;
			}
		}
		var count = positional.Count;
		if (name is "backup" or "version" && count != 0 || name == "status" && count > 1
			|| name is "backups" or "unlock" && count != 1 || name == "verify" && count is not (1 or 2)
			|| name is "restore" or "recover" or "import-backup" && count != 2) return false;
		if (recovery != null && name != "recover" || name == "recover" && recovery == null) return false;
		if (acceptInterrupted && name != "verify") return false;
		if (confirm && name is "status" or "backups" or "backup" or "version") return false;
		var id = count > 0 ? positional[0] : null;
		var value = count > 1 ? positional[1] : null;
		if (id != null && !DatabaseIdentity.IsSafeIdentifier(id)) return false;
		if (value != null && name != "import-backup" && !DatabaseIdentity.IsSafeIdentifier(value)) return false;
		command = new DatabaseCommand(name, id, value, confirm, acceptInterrupted, recovery);
		return true;
	}
}
