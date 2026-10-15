using Discord;
using Pengin1011.Core;
using Pengin1011.Core.Logging;

namespace Pengin1011.Services.Discord;

public sealed record ForwarderChannelFailure(ulong ChannelId, int Count, string LastError);

public sealed record ForwarderStopResult(
	bool Stopped,
	bool Pending,
	int EntriesDeliveredAny,
	int EntriesDropped,
	int ChannelBatchSucceeded,
	int ChannelBatchPartial,
	int ChannelBatchFailed,
	int RemainingQueued,
	int ActiveSends,
	IReadOnlyList<ForwarderChannelFailure> Failures);

public static class LoggerChannelForwarder {
	public const int QueueLimit = 1000;
	public const int FlushBatchLimit = 20;
	public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
	public static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(750);
	public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

	private static readonly object _gate = new();
	private static readonly AsyncLocal<bool> _inForward = new();
	private static Session? _session;
	private static ForwarderStopResult? _lastStop;

	internal static Func<bool> ConnectedForTest = static () => DiscordGateway.ConnectionState == ConnectionState.Connected;
	internal static Func<Action<LogEntry>, IDisposable> SubscribeForTest = Logger.Subscribe;
	internal static Func<ulong, CancellationToken, Task<IMessageChannel?>> ResolverForTest = static async (channelId, ct) => await Channels.ResolveAsync(channelId, ct) as IMessageChannel;
	internal static Func<IMessageChannel, string, CancellationToken, Task<MessageSendResult>> SenderForTest = static async (channel, text, ct) => await Messages.SendAsync(channel, text, ct: ct);
	internal static TimeSpan FlushDelayForTest = FlushDelay;

	internal static void ResetForTest() {
		ConnectedForTest = static () => DiscordGateway.ConnectionState == ConnectionState.Connected;
		SubscribeForTest = Logger.Subscribe;
		ResolverForTest = static async (channelId, ct) => await Channels.ResolveAsync(channelId, ct) as IMessageChannel;
		SenderForTest = static async (channel, text, ct) => await Messages.SendAsync(channel, text, ct: ct);
		FlushDelayForTest = FlushDelay;
		_lastStop = null;
	}

	public static bool Running {
		get {
			lock (_gate) return _session != null;
		}
	}

	internal static (int Queued, int Dropped) QueueSnapshotForTest {
		get {
			lock (_gate) return _session == null ? (0, 0) : (_session.Queue.Count, _session.Dropped);
		}
	}

	internal static ForwarderStopResult? LastStopForTest => _lastStop;

	private sealed class Session {
		public required IReadOnlyList<ulong> Channels { get; init; }
		public Queue<string> Queue { get; } = new();
		public CancellationTokenSource Cts { get; } = new();
		public IDisposable Subscription { get; set; } = null!;
		public Task Worker { get; set; } = Task.CompletedTask;
		public Task<ForwarderStopResult>? StopTask { get; set; }
		public int Dropped { get; set; }
		public int DeliveredAny { get; set; }
		public int ChannelBatchSucceeded { get; set; }
		public int ChannelBatchPartial { get; set; }
		public int ChannelBatchFailed { get; set; }
		public int ActiveSends { get; set; }
		public bool Draining { get; set; }
		public Dictionary<ulong, ForwarderChannelFailure> Failures { get; } = [];
	}

	public static void Start(IReadOnlyList<ulong> channels) {
		if (channels.Count == 0) return;
		lock (_gate) {
			if (_session != null) return;
			var session = new Session { Channels = channels.Distinct().ToArray() };
			_lastStop = null;
			session.Subscription = SubscribeForTest(entry => OnLog(session, entry));
			_session = session;
			session.Worker = Task.Run(() => RunAsync(session, session.Cts.Token));
		}
		WriteLocal(() => Logger.Info(typeof(LoggerChannelForwarder), Loc.Format("LoggerForwarderStarted", channels.Count)));
	}

	public static Task<ForwarderStopResult> StopAsync(TimeSpan budget) {
		Session? session;
		Task<ForwarderStopResult> stopTask;
		lock (_gate) {
			session = _session;
			if (session == null) return Task.FromResult(_lastStop ?? EmptyResult());
			if (session.StopTask == null) {
				session.Draining = true;
				session.StopTask = Task.Run(() => StopSessionAsync(session));
			}
			stopTask = session.StopTask;
		}
		return WaitForStopAsync(session, stopTask, budget);
	}

	private static async Task<ForwarderStopResult> WaitForStopAsync(Session session, Task<ForwarderStopResult> stopTask, TimeSpan budget) {
		try {
			return await stopTask.WaitAsync(budget);
		} catch (TimeoutException) {
			if (stopTask.IsCompleted) return await stopTask;
			var pending = BuildResult(session, stopped: false, pending: true);
			WriteLocal(() => Logger.Error(typeof(LoggerChannelForwarder), Loc.Format("LoggerForwarderStopPending", pending.RemainingQueued, pending.ActiveSends)));
			return pending;
		}
	}

	private static async Task<ForwarderStopResult> StopSessionAsync(Session session) {
		try {
			session.Subscription.Dispose();
		} catch (Exception e) {
			WriteLocal(() => Logger.Error(typeof(LoggerChannelForwarder), e, Loc.Get("LoggerForwarderUnsubscribeFailed")));
		}
		session.Cts.CancelAfter(StopGrace);
		await session.Worker;
		var result = BuildResult(session, stopped: true, pending: false);
		lock (_gate) {
			if (ReferenceEquals(_session, session)) _session = null;
			_lastStop = result;
		}
		session.Cts.Dispose();
		WriteLocal(() => Logger.Info(typeof(LoggerChannelForwarder), Loc.Format("LoggerForwarderStopping", result.EntriesDeliveredAny, result.EntriesDropped, result.RemainingQueued)));
		return result;
	}

	private static ForwarderStopResult BuildResult(Session session, bool stopped, bool pending) {
		lock (_gate) {
			return new ForwarderStopResult(
				stopped,
				pending,
				session.DeliveredAny,
				session.Dropped,
				session.ChannelBatchSucceeded,
				session.ChannelBatchPartial,
				session.ChannelBatchFailed,
				session.Queue.Count,
				session.ActiveSends,
				[.. session.Failures.Values]);
		}
	}

	private static ForwarderStopResult EmptyResult() {
		return new ForwarderStopResult(true, false, 0, 0, 0, 0, 0, 0, 0, []);
	}

	private static void OnLog(Session session, LogEntry entry) {
		if (_inForward.Value) return;
		lock (_gate) {
			if (!ReferenceEquals(_session, session) || session.Draining) return;
			while (session.Queue.Count >= QueueLimit) {
				session.Queue.Dequeue();
				session.Dropped++;
			}
			session.Queue.Enqueue(Format(entry));
		}
	}

	private static async Task RunAsync(Session session, CancellationToken ct) {
		try {
			while (!ct.IsCancellationRequested) {
				string[] batch;
				lock (_gate) {
					var connected = ConnectedForTest();
					var count = connected ? Math.Min(session.Queue.Count, FlushBatchLimit) : 0;
					batch = new string[count];
					for (var i = 0; i < count; i++) batch[i] = session.Queue.Dequeue();
					if (session.Draining && batch.Length == 0 && (session.Queue.Count == 0 || !connected)) return;
				}
				if (batch.Length == 0) {
					await Task.Delay(PollInterval, ct);
					continue;
				}
				await FlushAsync(session, batch, ct);
				await Task.Delay(FlushDelayForTest, ct);
			}
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			WriteLocal(() => Logger.Warn(typeof(LoggerChannelForwarder), Loc.Get("LoggerForwarderSummary")));
		} catch (Exception e) {
			WriteLocal(() => Logger.Error(typeof(LoggerChannelForwarder), e, Loc.Format("LoggerForwarderWorkerFailed", e.GetType().Name, e.Message)));
		} finally {
			lock (_gate) {
				session.Draining = true;
				session.Dropped += session.Queue.Count;
				session.Queue.Clear();
			}
		}
	}

	private static async Task FlushAsync(Session session, string[] batch, CancellationToken ct) {
		var text = string.Join("\n", batch);
		var deliveredAny = false;
		_inForward.Value = true;
		try {
			foreach (var channelId in session.Channels) {
				if (ct.IsCancellationRequested) break;
				var status = MessageSendStatus.Failed;
				lock (_gate) session.ActiveSends++;
				try {
					var channel = await ResolverForTest(channelId, ct);
					if (channel == null) {
						ReportChannelFailure(session, channelId, Loc.Format("LoggerForwarderChannelMissing", channelId), null);
						continue;
					}
					var result = await SenderForTest(channel, text, ct);
					status = result.Status;
					if (status == MessageSendStatus.Succeeded) {
						deliveredAny = true;
					} else if (status == MessageSendStatus.Partial) {
						ReportChannelFailure(session, channelId, Loc.Format("LoggerForwarderSendPartial", channelId, result.Sent.Count, result.PlannedChunks), null);
					} else {
						ReportChannelFailure(session, channelId, Loc.Format("LoggerForwarderSendFailed", channelId, status), null);
					}
				} catch (OperationCanceledException e) {
					ReportChannelFailure(session, channelId, Loc.Format("LoggerForwarderSendCancelled", channelId), e);
					if (ct.IsCancellationRequested) break;
				} catch (Exception e) {
					ReportChannelFailure(session, channelId, Loc.Format("LoggerForwarderSendFailed", channelId, e.GetType().Name), e);
				} finally {
					lock (_gate) {
						session.ActiveSends--;
						if (status == MessageSendStatus.Succeeded) session.ChannelBatchSucceeded++;
						else if (status == MessageSendStatus.Partial) session.ChannelBatchPartial++;
						else session.ChannelBatchFailed++;
					}
				}
			}
		} finally {
			_inForward.Value = false;
			lock (_gate) {
				if (deliveredAny) session.DeliveredAny += batch.Length;
				else session.Dropped += batch.Length;
			}
		}
	}

	private static void ReportChannelFailure(Session session, ulong channelId, string message, Exception? exception) {
		lock (_gate) {
			session.Failures.TryGetValue(channelId, out var previous);
			session.Failures[channelId] = new ForwarderChannelFailure(channelId, (previous?.Count ?? 0) + 1, message);
		}
		if (exception != null) Logger.Error(typeof(LoggerChannelForwarder), exception, message);
		else Logger.Error(typeof(LoggerChannelForwarder), message);
	}

	private static void WriteLocal(Action write) {
		var wasForwarding = _inForward.Value;
		_inForward.Value = true;
		try {
			write();
		} finally {
			_inForward.Value = wasForwarding;
		}
	}

	private static string Format(LogEntry entry) {
		var exception = entry.Exception == null ? "" : $" [{entry.Exception.GetType().Name}]";
		return $"[{entry.Time:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level}] [{entry.Source.Name}]{exception} {Loc.Get("LoggerForwarderSummary")}";
	}
}
