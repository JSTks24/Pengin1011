using Pengin1011.Core.Logging;
using Pengin1011.Core.Modules;

namespace Pengin1011.Core;

public enum WorkRejectReason {
	Paused,
	Exiting,
	Full,
}

internal enum WorkAcceptState {
	Open,
	Paused,
	Exiting,
}

public sealed record TrackedHandler(LoadedModule? Owner, Func<CancellationToken, Task> Handler);

public sealed class WorkTracker : IDisposable {
	private readonly object _gate = new();
	private readonly List<WorkItem> _items = [];
	private WorkAcceptState _accepting = WorkAcceptState.Paused;
	private bool _disposed;

	public int MaxConcurrent { get; }

	public WorkTracker(int maxConcurrent) {
		MaxConcurrent = Math.Max(1, maxConcurrent);
	}

	public bool Accepting {
		get {
			lock (_gate) {
				return _accepting == WorkAcceptState.Open;
			}
		}
	}

	internal bool ExitClosedForTest {
		get {
			lock (_gate) {
				return _accepting == WorkAcceptState.Exiting;
			}
		}
	}

	public int ActiveCount {
		get {
			lock (_gate) {
				return _items.Count;
			}
		}
	}

	public void Open() {
		lock (_gate) {
			if (_accepting == WorkAcceptState.Exiting) {
				Logger.Error(typeof(WorkTracker), "退出关闭后拒绝恢复接单");
				return;
			}
			_accepting = WorkAcceptState.Open;
		}
	}

	public void RequestExitClose() {
		lock (_gate) {
			_accepting = WorkAcceptState.Exiting;
		}
	}

	public WorkItem? TryBegin(IReadOnlyList<TrackedHandler> handlers, out WorkRejectReason reason) {
		lock (_gate) {
			if (_disposed || _accepting != WorkAcceptState.Open) {
				reason = _accepting == WorkAcceptState.Exiting ? WorkRejectReason.Exiting : WorkRejectReason.Paused;
				return null;
			}
			if (_items.Count >= MaxConcurrent) {
				reason = WorkRejectReason.Full;
				return null;
			}
			var item = new WorkItem(handlers);
			item.Task = Task.Run(() => RunAsync(item));
			_items.Add(item);
			reason = default;
			return item;
		}
	}

	public void CancelActive() {
		WorkItem[] items;
		lock (_gate) {
			items = [.. _items];
		}
		foreach (var item in items) {
			item.Cancel();
		}
	}

	public async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken ct = default) {
		Task drainTask;
		lock (_gate) {
			drainTask = Task.WhenAll(_items.Select(item => item.Task));
		}
		try {
			await drainTask.WaitAsync(timeout, ct);
			return true;
		} catch (TimeoutException) {
			return false;
		}
	}

	private async Task RunAsync(WorkItem item) {
		try {
			foreach (var handler in item.Handlers) {
				if (handler.Owner != null && handler.Owner.State != ModuleState.Ready) continue;
				using var linked = handler.Owner == null
					? null
					: CancellationTokenSource.CreateLinkedTokenSource(item.Token, handler.Owner.Lifecycle.Token);
				var token = linked?.Token ?? item.Token;
				try {
					await handler.Handler(token);
				} catch (OperationCanceledException) when (token.IsCancellationRequested) {
				} catch (Exception e) {
					Logger.Error(typeof(WorkTracker), e, $"业务任务异常：{handler.Handler.Method.DeclaringType?.Name}.{handler.Handler.Method.Name}");
				}
			}
		} finally {
			bool removed;
			lock (_gate) {
				removed = _items.Remove(item);
			}
			item.Dispose();
			if (!removed) {
				Logger.Error(typeof(WorkTracker), "工作条目完成时未在集合中找到（调度状态异常）");
			}
		}
	}

	public void Dispose() {
		lock (_gate) {
			_disposed = true;
		}
	}

	public sealed class WorkItem : IDisposable {
		private readonly CancellationTokenSource _cts = new();

		public WorkItem(IReadOnlyList<TrackedHandler> handlers) {
			Handlers = handlers;
		}

		public IReadOnlyList<TrackedHandler> Handlers { get; }
		public Task Task { get; internal set; } = Task.CompletedTask;

		public CancellationToken Token => _cts.Token;

		internal void Cancel() {
			try {
				_cts.Cancel();
			} catch (ObjectDisposedException) {
			}
		}

		public void Dispose() {
			_cts.Dispose();
		}
	}
}
