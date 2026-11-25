namespace Pengin1011.Core;

public enum DatabaseState {
	NotPresent,
	Unprepared,
	Preparing,
	Prepared,
	Failed,
	MaintenanceRequired,
}

internal enum RestoreOutcome {
	Completed,
	Rejected,
	Failed,
}
