namespace Pengin1011.Core;

public static class HostExit {
	private static readonly object _gate = new();
	private static Task<ExitCoordinator.ExitReport>? _task;

	public static bool Requested {
		get {
			lock (_gate) {
				return _task != null;
			}
		}
	}

	public static Task<ExitCoordinator.ExitReport> StopAsync() {
		lock (_gate) {
			return _task ??= new ExitCoordinator().RunAsync();
		}
	}

	public static int CodeFor(bool started, ExitCoordinator.ExitReport report) {
		return started && report.Success ? 0 : 1;
	}

	internal static void ResetForTest() {
		lock (_gate) {
			_task = null;
		}
	}
}
