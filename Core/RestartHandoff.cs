using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Pengin1011.Core.Logging;

namespace Pengin1011.Core;

internal static class RestartHandoff {
	private const string PipeVariable = "PENGIN1011_RESTART_PIPE";
	private const string ParentVariable = "PENGIN1011_RESTART_PARENT";
	private static readonly TimeSpan _startupBudget = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan _shutdownBudget = TimeSpan.FromSeconds(120);

	internal static async Task<bool> RunAsync(ProcessStartInfo info, CancellationToken ct = default, TimeSpan? startupBudget = null, TimeSpan? shutdownBudget = null) {
		using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
		startup.CancelAfter(startupBudget ?? _startupBudget);
		var name = "pengin1011-restart-" + Guid.NewGuid().ToString("N");
		using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		StreamReader? reader = null;
		StreamWriter? writer = null;
		info.Environment[PipeVariable] = name;
		info.Environment[ParentVariable] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
		Process? process = null;
		var released = false;
		var success = false;
		var gateHeld = false;
		try {
			await Databases.BackupGate.WaitAsync(startup.Token);
			gateHeld = true;
			HostMaintenance.DataAccessPaused = true;
			process = Process.Start(info);
			if (process == null) return false;
			var exited = process.WaitForExitAsync();
			await WhileRunningAsync(pipe.WaitForConnectionAsync(startup.Token), exited, startup.Token);
			reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
			writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
			Databases.ReleaseOwnership();
			released = true;
			await writer.WriteLineAsync("takeover".AsMemory(), startup.Token);
			var response = reader.ReadLineAsync(startup.Token).AsTask();
			await WhileRunningAsync(response, exited, startup.Token);
			if (await response != "ready" || process.HasExited) return false;
			await writer.WriteLineAsync("commit".AsMemory(), startup.Token);
			success = true;
			return true;
		} catch (Exception e) {
			Logger.Error(typeof(RestartHandoff), e, Loc.Get("RestartFailed"));
			return false;
		} finally {
			try {
				if (!success) {
					if (process != null) await StopChildAsync(process, pipe, writer, shutdownBudget ?? _shutdownBudget);
					if (gateHeld && (!released || process is { HasExited: true })) {
						for (var attempt = 0; ; attempt++) {
							try {
								Databases.AcquireOwnership();
								HostMaintenance.DataAccessPaused = false;
								break;
							} catch (IOException) when (attempt < 10) {
								await Task.Delay(50);
							} catch (Exception e) {
								Logger.Error(typeof(RestartHandoff), e, Loc.Get("RestartFailed"));
								break;
							}
						}
					}
				}
				if (writer != null) {
					try {
						await writer.DisposeAsync();
					} catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) {
						Logger.Error(typeof(RestartHandoff), e, Loc.Get("RestartFailed"));
					}
				}
			} finally {
				try {
					reader?.Dispose();
				} finally {
					if (gateHeld) Databases.BackupGate.Release();
					process?.Dispose();
				}
			}
		}
	}

	private static async Task WhileRunningAsync(Task operation, Task exited, CancellationToken ct) {
		if (await Task.WhenAny(operation, exited).WaitAsync(ct) == exited) throw new IOException(Loc.Get("RestartFailed"));
		await operation;
	}

	private static async Task StopChildAsync(Process process, NamedPipeServerStream pipe, StreamWriter? writer, TimeSpan budget) {
		try {
			if (process.HasExited) return;
			if (pipe.IsConnected && writer != null) {
				try {
					using var send = new CancellationTokenSource(TimeSpan.FromSeconds(5));
					await writer.WriteLineAsync("stop".AsMemory(), send.Token);
				} catch (Exception e) when (e is IOException or OperationCanceledException) {
					Logger.Error(typeof(RestartHandoff), e, Loc.Get("RestartFailed"));
				}
			}
			try {
				await process.WaitForExitAsync().WaitAsync(budget);
			} catch (TimeoutException) {
				process.Kill();
				await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
			}
		} catch (Exception e) {
			Logger.Error(typeof(RestartHandoff), e, Loc.Get("RestartFailed"));
		}
	}

	internal static async Task<ChildSession?> ConnectAsync(Action requestStop, CancellationToken ct) {
		var name = Environment.GetEnvironmentVariable(PipeVariable);
		var parentText = Environment.GetEnvironmentVariable(ParentVariable);
		Environment.SetEnvironmentVariable(PipeVariable, null);
		Environment.SetEnvironmentVariable(ParentVariable, null);
		if (name == null && parentText == null) return null;
		if (string.IsNullOrWhiteSpace(name) || !int.TryParse(parentText, out var parentId) || parentId <= 0 || parentId == Environment.ProcessId) {
			throw new InvalidDataException(Loc.Get("RestartFailed"));
		}
		var session = new ChildSession(name, parentId, requestStop);
		try {
			await session.ConnectAsync(ct);
			return session;
		} catch {
			await session.DisposeAsync();
			throw;
		}
	}

	internal sealed class ChildSession : IAsyncDisposable {
		private readonly NamedPipeClientStream _pipe;
		private readonly StreamReader _reader;
		private readonly StreamWriter _writer;
		private readonly Process _parent;
		private readonly Action _requestStop;
		private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private Task? _monitor;

		internal ChildSession(string name, int parentId, Action requestStop) {
			_parent = Process.GetProcessById(parentId);
			_pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			_reader = new StreamReader(_pipe, Encoding.UTF8, false, 1024, true);
			_writer = new StreamWriter(_pipe, new UTF8Encoding(false), 1024, true);
			_requestStop = requestStop;
		}

		internal async Task ConnectAsync(CancellationToken ct) {
			using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
			budget.CancelAfter(_startupBudget);
			await WhileRunningAsync(_pipe.ConnectAsync(budget.Token), _parent.WaitForExitAsync(), budget.Token);
			_writer.AutoFlush = true;
			if (await _reader.ReadLineAsync(budget.Token) != "takeover") throw new IOException(Loc.Get("RestartFailed"));
			_monitor = MonitorAsync();
		}

		private async Task MonitorAsync() {
			var committed = false;
			try {
				committed = await _reader.ReadLineAsync() == "commit";
			} catch (Exception e) when (e is IOException or ObjectDisposedException) {
				Logger.Error(typeof(RestartHandoff), e, Loc.Get("RestartFailed"));
			} finally {
				_decision.TrySetResult(committed);
				if (!committed) {
					try {
						_requestStop();
					} catch (Exception e) {
						Logger.Error(typeof(RestartHandoff), e, Loc.Get("CancelActiveWorkFailed"));
					}
				}
			}
		}

		internal async Task<bool> CompleteStartupAsync(bool started, CancellationToken ct) {
			await _writer.WriteLineAsync((started ? "ready" : "failed").AsMemory(), ct);
			if (!await _decision.Task.WaitAsync(_startupBudget, ct) || !started) return false;
			await _parent.WaitForExitAsync(ct).WaitAsync(_startupBudget, ct);
			return true;
		}

		public async ValueTask DisposeAsync() {
			try {
				await _writer.DisposeAsync();
			} catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) {
				Logger.Error(typeof(RestartHandoff), e, Loc.Get("RestartFailed"));
			}
			try {
				await _pipe.DisposeAsync();
				if (_monitor != null) await _monitor;
			} finally {
				_reader.Dispose();
				_parent.Dispose();
			}
		}
	}
}
