using Pengin1011.Core.Logging;

namespace Pengin1011.Core;

internal static class HostMaintenance {
	private static readonly object _gate = new();
	private static TaskCompletionSource<DatabaseCommand> _request = NewRequest();
	private static bool _busy;

	internal static bool Busy {
		get {
			lock (_gate) return _busy;
		}
	}

	internal static bool MaintenanceOnly { get; set; }
	internal static volatile bool DataAccessPaused;
	internal static Task<DatabaseCommand> Pending {
		get {
			lock (_gate) return _request.Task;
		}
	}

	internal static async Task<string> RequestAsync(DatabaseCommand command) {
		if (DataAccessPaused) return Loc.Get("MaintenanceBusy");
		if (HostExit.ResourcesUnsettled) return Loc.Get("MaintenanceUnavailable");
		await Databases.BackupGate.WaitAsync();
		try {
			if (DataAccessPaused) return Loc.Get("MaintenanceBusy");
			if (HostExit.ResourcesUnsettled) return Loc.Get("MaintenanceUnavailable");
			lock (_gate) {
				if (_busy) return Loc.Get("MaintenanceBusy");
			}
			if (command.Name == "restore" && !new[] { DatabaseBackupStore.RollingCategory, DatabaseBackupStore.PreMigrationCategory }
				.Any(category => Databases.ListBackups(command.Database!, category).Any(backup => backup.BackupId == command.Value))) {
				return Loc.Format("RestoreBackupInvalid", command.Value, "");
			}
			lock (_gate) {
				_busy = true;
				_request.SetResult(command);
				return Loc.Get("MaintenanceAccepted");
			}
		} finally {
			Databases.BackupGate.Release();
		}
	}

	internal static string? ProtectedRestoreBackupId(string databaseId) {
		lock (_gate) {
			if (!_busy || !_request.Task.IsCompletedSuccessfully) return null;
			var command = _request.Task.Result;
			return command.Name == "restore" && string.Equals(command.Database, databaseId, StringComparison.OrdinalIgnoreCase) ? command.Value : null;
		}
	}

	internal static Task<bool> ExecutePendingAsync(Func<Task<ExitCoordinator.ExitReport>> stop, Func<bool> restart) {
		return ExecutePendingWithRestartAsync(stop, () => Task.FromResult(restart()));
	}

	internal static async Task<bool> ExecutePendingWithRestartAsync(Func<Task<ExitCoordinator.ExitReport>> stop, Func<Task<bool>> restart) {
		Task<DatabaseCommand> request;
		lock (_gate) request = _request.Task;
		var command = await request;
		try {
			MaintenanceOnly = true;
			var stopped = await stop();
			if (!stopped.ResourcesSettled) {
				Logger.Error(typeof(HostMaintenance), Loc.Get("MaintenanceUnavailable"));
				return false;
			}
			MaintenanceResult result;
			switch (command.Name) {
				case "restore":
					var restored = await Databases.RestoreAsync(command.Database!, command.Value!);
					result = new MaintenanceResult(restored.Outcome == RestoreOutcome.Completed, restored.Reason, restored.OperationId);
					break;
				case "recover":
					result = command.Recovery == "--complete" ? Databases.CompleteRestore(command.Database!, command.Value!) : Databases.RollbackRestore(command.Database!, command.Value!);
					break;
				case "verify":
					result = Databases.Verify(command.Database!, command.AcceptInterrupted);
					break;
				case "unlock":
					result = Databases.Unlock(command.Database!);
					break;
				default:
					return false;
			}
			if (!result.Success) {
				Logger.Error(typeof(HostMaintenance), Loc.Format("DbCommandRejected", result.Reason));
				return false;
			}
			Logger.Info(typeof(HostMaintenance), Loc.Get("Restarting"));
			if (await restart()) return true;
			Logger.Error(typeof(HostMaintenance), Loc.Get("MaintenanceCompletedRestartFailed"));
			return false;
		} catch (Exception e) {
			Logger.Error(typeof(HostMaintenance), e, Loc.Get("MaintenanceUnavailable"));
			return false;
		} finally {
			lock (_gate) {
				_busy = false;
				_request = NewRequest();
			}
		}
	}

	internal static void ResetForTest() {
		lock (_gate) {
			_busy = false;
			_request = NewRequest();
			MaintenanceOnly = false;
			DataAccessPaused = false;
		}
	}

	private static TaskCompletionSource<DatabaseCommand> NewRequest() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
