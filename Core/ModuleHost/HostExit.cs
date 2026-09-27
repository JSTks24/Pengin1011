namespace QingQiu1011.Core;

public static class HostExit {
	private static readonly object Gate = new();
	private static Task<ExitCoordinator.ExitReport>? _task;

	public static bool Requested {
		get {
			lock (Gate) {
				return _task != null;
			}
		}
	}

	public static Task<ExitCoordinator.ExitReport> StopAsync() {
		lock (Gate) {
			return _task ??= new ExitCoordinator().RunAsync();
		}
	}

	public static int CodeFor(bool started, ExitCoordinator.ExitReport report) {
		return started && report.Success ? 0 : 1;
	}

	internal static void ResetForTest() {
		lock (Gate) {
			_task = null;
		}
	}
}
