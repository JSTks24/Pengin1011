using System.Runtime.InteropServices;
using Pengin1011.Core.Logging;

namespace Pengin1011.Core;

internal sealed class ShutdownSignals : IDisposable {
	private readonly Action _requestStop;
	private readonly Task _shutdownCompleted;
	private readonly PosixSignalRegistration _termination;
	private int _requests;

	internal ShutdownSignals(Action requestStop, Task shutdownCompleted) {
		_requestStop = requestStop;
		_shutdownCompleted = shutdownCompleted;
		Console.CancelKeyPress += OnCancel;
		_termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTermination);
	}

	internal bool RequestStop() {
		if (Interlocked.Increment(ref _requests) != 1) return false;
		try {
			_requestStop();
		} catch (Exception e) {
			Logger.Error(typeof(ShutdownSignals), e, Loc.Get("CancelActiveWorkFailed"));
		}
		return true;
	}

	private void OnCancel(object? sender, ConsoleCancelEventArgs args) {
		args.Cancel = RequestStop();
	}

	private void OnTermination(PosixSignalContext context) {
		var first = RequestStop();
		if (!OperatingSystem.IsWindows()) {
			context.Cancel = first;
			return;
		}
		if (first) _shutdownCompleted.Wait(TimeSpan.FromSeconds(4));
	}

	public void Dispose() {
		Console.CancelKeyPress -= OnCancel;
		_termination.Dispose();
	}
}
