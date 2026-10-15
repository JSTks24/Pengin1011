using Discord;
using Pengin1011.Core.Logging;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class LoggerChannelForwarderTests : IDisposable {
	private sealed class FakeSource;

	private sealed class CountingSubscription(IDisposable subscription, Action onDispose) : IDisposable {
		public void Dispose() {
			onDispose();
			subscription.Dispose();
		}
	}

	private readonly object _gate = new();
	private readonly List<string> _sent = [];
	private readonly FakeMessageChannel _channel = new();
	private bool _connected;
	private int _senderCalls;
	private MessageSendStatus _status = MessageSendStatus.Succeeded;
	private int _plannedChunks = 1;
	private bool _senderThrows;
	private bool _resolverThrows;

	public LoggerChannelForwarderTests() {
		LoggerChannelForwarder.ResetForTest();
		LoggerChannelForwarder.ConnectedForTest = () => _connected;
		LoggerChannelForwarder.ResolverForTest = (_, _) => {
			if (_resolverThrows) throw new InvalidOperationException("解析失败");
			return Task.FromResult<IMessageChannel?>(_channel);
		};
		LoggerChannelForwarder.SenderForTest = async (_, text, ct) => {
			Interlocked.Increment(ref _senderCalls);
			await Task.Yield();
			if (_senderThrows) throw new InvalidOperationException("发送失败");
			lock (_gate) _sent.Add(text);
			var sent = _status == MessageSendStatus.Failed ? [] : new List<IUserMessage>();
			return new MessageSendResult(_status, _plannedChunks, sent, _status == MessageSendStatus.Succeeded ? [] : [0]);
		};
	}

	public void Dispose() {
		LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
		LoggerChannelForwarder.ResetForTest();
	}

	private int SentCount {
		get { lock (_gate) return _sent.Count; }
	}

	private string LastSent {
		get { lock (_gate) return _sent[^1]; }
	}

	private static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout) {
		var deadline = DateTimeOffset.UtcNow + timeout;
		while (DateTimeOffset.UtcNow < deadline) {
			if (condition()) return true;
			await Task.Delay(50);
		}
		return condition();
	}

	[Fact]
	public void Start_EmptyChannels_DoesNotRun() {
		LoggerChannelForwarder.Start([]);

		Assert.False(LoggerChannelForwarder.Running);
	}

	[Fact]
	public async Task Start_ForwardsLogsToEveryChannel_WhenConnected() {
		_connected = true;
		LoggerChannelForwarder.Start([111, 222]);
		Logger.Info(typeof(FakeSource), "转发内容");

		Assert.True(await WaitAsync(() => SentCount >= 2, TimeSpan.FromSeconds(5)), "两个频道都应收到转发");
		lock (_gate) {
			Assert.Equal(2, _sent.Count);
			Assert.All(_sent, text => Assert.Contains("[FakeSource]", text));
			Assert.All(_sent, text => Assert.DoesNotContain("转发内容", text));
			Assert.All(_sent, text => Assert.Contains("[Info]", text));
		}
	}

	[Fact]
	public async Task NotConnected_HoldsQueue_ThenFlushesAfterConnect() {
		_connected = false;
		LoggerChannelForwarder.Start([111]);
		Logger.Info(typeof(FakeSource), "积压内容");
		await Task.Delay(1200);
		Assert.Equal(0, SentCount);

		_connected = true;

		Assert.True(await WaitAsync(() => SentCount >= 1, TimeSpan.FromSeconds(5)), "连接后应补发积压");
		Assert.Contains("[FakeSource]", LastSent);
	}

	[Fact]
	public async Task StopAsync_DrainsRemainingEntries() {
		_connected = true;
		LoggerChannelForwarder.Start([111]);
		for (var i = 0; i < 5; i++) Logger.Info(typeof(FakeSource), $"条目{i}");

		var result = await LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5));

		Assert.True(result.Stopped);
		Assert.False(result.Pending);
		Assert.False(LoggerChannelForwarder.Running);
		Assert.Equal(1, SentCount);
		Assert.Equal(5, LastSent.Split('\n').Length);
	}

	[Fact]
	public async Task SenderLogs_DoesNotRecirculateIntoForwarding() {
		_connected = true;
		LoggerChannelForwarder.SenderForTest = async (_, _, _) => {
			Interlocked.Increment(ref _senderCalls);
			await Task.Yield();
			Logger.Error(typeof(FakeSource), "转发链路内部错误");
			return new MessageSendResult(MessageSendStatus.Succeeded, 1, [], []);
		};
		LoggerChannelForwarder.Start([111]);
		Logger.Info(typeof(FakeSource), "原始条目");

		Assert.True(await WaitAsync(() => Volatile.Read(ref _senderCalls) >= 1, TimeSpan.FromSeconds(5)), "首次转发应发生");
		await Task.Delay(2000);
		Assert.Equal(1, Volatile.Read(ref _senderCalls));
	}

	[Fact]
	public void Overflow_DropsOldestEntries() {
		var infoPath = Path.Combine(Path.GetTempPath(), $"qq1011_{Guid.NewGuid():N}", "logs", "info.log");
		Logger.SetInfoPathForTest(infoPath);
		try {
			_connected = false;
			LoggerChannelForwarder.Start([111]);
			for (var i = 0; i < LoggerChannelForwarder.QueueLimit + 5; i++) Logger.Info(typeof(FakeSource), $"条目{i}");
			var (queued, dropped) = LoggerChannelForwarder.QueueSnapshotForTest;
			Assert.Equal(LoggerChannelForwarder.QueueLimit, queued);
			Assert.Equal(5, dropped);
		} finally {
			Logger.SetInfoPathForTest(null);
			var directory = Path.GetDirectoryName(Path.GetFullPath(infoPath))!;
			if (Directory.Exists(directory)) Directory.Delete(directory, true);
		}
	}

	[Fact]
	public async Task UnresolvableChannel_IsSkippedWithoutThrowing() {
		_connected = true;
		LoggerChannelForwarder.ResolverForTest = (_, _) => Task.FromResult<IMessageChannel?>(null);
		LoggerChannelForwarder.Start([111]);
		Logger.Info(typeof(FakeSource), "内容");
		await Task.Delay(1500);
		Assert.Equal(0, SentCount);
	}

	[Fact]
	public async Task L01_FailedResult_IsNotCountedAsDelivered() {
		_connected = true;
		_status = MessageSendStatus.Failed;
		LoggerChannelForwarder.Start([111]);
		Logger.Info(typeof(FakeSource), "失败条目");

		var result = await LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5));

		Assert.True(result.Stopped);
		Assert.Equal(0, result.EntriesDeliveredAny);
		Assert.Equal(1, result.ChannelBatchFailed);
		Assert.Equal(0, result.ChannelBatchSucceeded);
		Assert.NotEmpty(result.Failures);
	}

	[Fact]
	public async Task L01_PartialResult_IsNotCountedAsSucceeded() {
		_connected = true;
		_status = MessageSendStatus.Partial;
		_plannedChunks = 3;
		LoggerChannelForwarder.Start([111]);
		Logger.Info(typeof(FakeSource), "部分条目");

		var result = await LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(0, result.EntriesDeliveredAny);
		Assert.Equal(1, result.ChannelBatchPartial);
		Assert.Equal(0, result.ChannelBatchSucceeded);
	}

	[Fact]
	public async Task L02_OneFullSuccessAmongFailures_CountsEntriesDeliveredAny() {
		_connected = true;
		var results = new Queue<MessageSendResult>([
			new MessageSendResult(MessageSendStatus.Succeeded, 1, [], []),
			new MessageSendResult(MessageSendStatus.Failed, 1, [], [0]),
		]);
		LoggerChannelForwarder.SenderForTest = (_, _, _) => {
			lock (_gate) return Task.FromResult(results.Count > 0 ? results.Dequeue() : new MessageSendResult(MessageSendStatus.Failed, 1, [], [0]));
		};
		LoggerChannelForwarder.Start([111, 222]);
		Logger.Info(typeof(FakeSource), "混合条目");

		var stop = LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5));
		var result = await stop;

		Assert.Equal(1, result.EntriesDeliveredAny);
		Assert.Equal(1, result.ChannelBatchSucceeded);
		Assert.Equal(1, result.ChannelBatchFailed);
		Assert.Equal(0, result.ChannelBatchPartial);
	}

	[Fact]
	public async Task L03_ResolverThrows_IsolatedToThatChannelAndWorkerSurvives() {
		_connected = true;
		_resolverThrows = true;
		LoggerChannelForwarder.Start([111]);
		Logger.Info(typeof(FakeSource), "解析异常条目");

		var result = await LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5));

		Assert.False(LoggerChannelForwarder.Running);
		Assert.Equal(0, result.EntriesDeliveredAny);
		Assert.NotEmpty(result.Failures);
	}

	[Fact]
	public async Task L03_SenderThrows_IsolatedToSingleChannel() {
		_connected = true;
		_senderThrows = true;
		LoggerChannelForwarder.Start([111]);
		Logger.Info(typeof(FakeSource), "发送异常条目");

		var result = await LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(0, result.EntriesDeliveredAny);
		Assert.Equal(1, result.ChannelBatchFailed);
		Assert.NotEmpty(result.Failures);
	}

	[Fact]
	public async Task L04_PendingSender_ReportsPendingWithoutPretendingStopped() {
		_connected = true;
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		LoggerChannelForwarder.SenderForTest = async (_, _, _) => {
			Interlocked.Increment(ref _senderCalls);
			entered.TrySetResult();
			await gate.Task;
			return new MessageSendResult(MessageSendStatus.Succeeded, 1, [], []);
		};
		LoggerChannelForwarder.Start([111]);
		Logger.Info(typeof(FakeSource), "阻塞条目");
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

		var result = await LoggerChannelForwarder.StopAsync(TimeSpan.FromMilliseconds(200));

		Assert.False(result.Stopped);
		Assert.True(result.Pending);
		Assert.Equal(0, result.EntriesDeliveredAny);
		Assert.True(LoggerChannelForwarder.Running, "未真实结束时会话必须保留");

		LoggerChannelForwarder.Start([222]);
		Assert.True(LoggerChannelForwarder.Running);

		gate.SetResult();
		var final = await LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5));
		Assert.True(final.Stopped);
		Assert.False(LoggerChannelForwarder.Running);
	}

	[Fact]
	public async Task L05_NewSessionAfterRealStop_DoesNotShareState() {
		_connected = true;
		LoggerChannelForwarder.Start([111]);
		Logger.Info(typeof(FakeSource), "第一会话");
		var first = await LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5));
		Assert.True(first.Stopped);

		LoggerChannelForwarder.Start([111]);
		Logger.Info(typeof(FakeSource), "第二会话");
		var second = await LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5));

		Assert.True(second.Stopped);
		Assert.Equal(first.EntriesDeliveredAny, second.EntriesDeliveredAny);
	}

	[Fact]
	public async Task L05_ConcurrentStops_ShareOneOutcome() {
		_connected = true;
		LoggerChannelForwarder.Start([111]);
		Logger.Info(typeof(FakeSource), "并发停止");

		var stops = Enumerable.Range(0, 4).Select(_ => LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5))).ToArray();
		var results = await Task.WhenAll(stops);

		Assert.All(results, result => Assert.True(result.Stopped));
		Assert.False(LoggerChannelForwarder.Running);
	}

	[Fact]
	public async Task L06_QueueRemainingIsObservableSeparatelyFromDelivery() {
		_connected = false;
		LoggerChannelForwarder.Start([111]);
		Logger.Info(typeof(FakeSource), "未连接条目");

		var result = await LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(0, result.EntriesDeliveredAny);
		Assert.True(result.EntriesDropped >= 1);
		Assert.Equal(0, result.RemainingQueued);
	}

	[Fact]
	public async Task L08_LongFailureHistory_IsBoundedPerChannel() {
		_connected = false;
		_status = MessageSendStatus.Failed;
		LoggerChannelForwarder.FlushDelayForTest = TimeSpan.Zero;
		LoggerChannelForwarder.Start([111, 222]);
		for (var i = 0; i < LoggerChannelForwarder.QueueLimit; i++) Logger.Info(typeof(FakeSource), $"失败条目{i}");
		_connected = true;

		var result = await LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5));

		Assert.True(result.Stopped);
		Assert.Equal(LoggerChannelForwarder.QueueLimit, result.EntriesDropped);
		Assert.Equal(100, result.ChannelBatchFailed);
		Assert.Equal(2, result.Failures.Count);
		Assert.All(result.Failures, failure => Assert.Equal(50, failure.Count));
		Assert.Equal(new ulong[] { 111, 222 }, result.Failures.Select(failure => failure.ChannelId).Order().ToArray());
	}

	[Fact]
	public async Task L09_ConcurrentStops_ShareCompletionAndKeepCallerBudgetsIndependent() {
		_connected = true;
		var subscriptions = Logger.SubscriberCountForTest;
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var cancelled = 0;
		var disposed = 0;
		LoggerChannelForwarder.SubscribeForTest = handler => new CountingSubscription(Logger.Subscribe(handler), () => Interlocked.Increment(ref disposed));
		LoggerChannelForwarder.SenderForTest = async (_, _, ct) => {
			using var registration = ct.Register(() => Interlocked.Increment(ref cancelled));
			entered.TrySetResult();
			await release.Task;
			return new MessageSendResult(MessageSendStatus.Succeeded, 1, [], []);
		};
		LoggerChannelForwarder.Start([111]);
		Logger.Info(typeof(FakeSource), "共享停止");
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

		var shortWait = await LoggerChannelForwarder.StopAsync(TimeSpan.FromMilliseconds(50));
		var waits = Enumerable.Range(0, 4).Select(_ => LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5))).ToArray();
		Assert.True(shortWait.Pending);
		Assert.False(shortWait.Stopped);
		Assert.Equal(0, Volatile.Read(ref cancelled));
		Assert.True(await WaitAsync(() => Logger.SubscriberCountForTest == subscriptions, TimeSpan.FromSeconds(1)));
		release.SetResult();
		var results = await Task.WhenAll(waits);

		Assert.All(results, result => Assert.Same(results[0], result));
		Assert.True(results[0].Stopped);
		Assert.Equal(1, results[0].EntriesDeliveredAny);
		Assert.Equal(subscriptions, Logger.SubscriberCountForTest);
		Assert.Equal(1, Volatile.Read(ref disposed));
		Assert.False(LoggerChannelForwarder.Running);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task L10_CancelledBatch_FinalizesDeliveryAndRemainingQueue(bool firstChannelSucceeds) {
		_connected = false;
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var sends = 0;
		LoggerChannelForwarder.SenderForTest = async (_, _, ct) => {
			if (firstChannelSucceeds && Interlocked.Increment(ref sends) == 1) {
				return new MessageSendResult(MessageSendStatus.Succeeded, 1, [], []);
			}
			entered.TrySetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
			return MessageSendResult.Empty;
		};
		LoggerChannelForwarder.Start([111, 222]);
		for (var i = 0; i < 27; i++) Logger.Info(typeof(FakeSource), $"待取消{i}");
		_connected = true;
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

		var result = await LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5));

		Assert.True(result.Stopped);
		Assert.False(result.Pending);
		Assert.Equal(firstChannelSucceeds ? 20 : 0, result.EntriesDeliveredAny);
		Assert.Equal(firstChannelSucceeds ? 7 : 27, result.EntriesDropped);
		Assert.Equal(firstChannelSucceeds ? 1 : 0, result.ChannelBatchSucceeded);
		Assert.Equal(1, result.ChannelBatchFailed);
		Assert.Equal(0, result.ActiveSends);
		Assert.Equal(0, result.RemainingQueued);
		Assert.Single(result.Failures);
	}

	[Fact]
	public async Task L11_RemoteSummary_ExcludesRawMessagesAndExceptionsButKeepsLocalDetails() {
		_connected = true;
		var directory = Path.Combine(Path.GetTempPath(), $"pengin1011_log_{Guid.NewGuid():N}");
		var path = Path.Combine(directory, "error.log");
		Logger.SetLogPathForTest(path);
		const string message = "token=fictional-token-secret https://user:password@example.invalid/path?key=fictional-key";
		const string detail = "{\"credentials\":\"fictional-exception-secret\"}";
		try {
			LoggerChannelForwarder.Start([111]);
			Logger.Error(typeof(FakeSource), new InvalidOperationException(detail), message);

			var result = await LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5));

			Assert.Equal(1, result.EntriesDeliveredAny);
			Assert.Contains("[Error] [FakeSource] [InvalidOperationException]", LastSent);
			Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]", LastSent);
			Assert.DoesNotContain(message, LastSent);
			Assert.DoesNotContain(detail, LastSent);
			Assert.DoesNotContain("fictional", LastSent);
			Assert.DoesNotContain("password", LastSent);
			var local = File.ReadAllText(path);
			Assert.Contains(message, local);
			Assert.Contains(detail, local);
		} finally {
			Logger.SetLogPathForTest(null);
			if (Directory.Exists(directory)) Directory.Delete(directory, true);
		}
	}

	[Fact]
	public async Task L12_WorkerFailure_StopsAcceptingEntriesAndAccountsQueuedEntries() {
		_connected = false;
		LoggerChannelForwarder.Start([111]);
		for (var i = 0; i < 7; i++) Logger.Info(typeof(FakeSource), $"工作器失败前{i}");
		LoggerChannelForwarder.ConnectedForTest = () => throw new InvalidOperationException("工作器失败");
		Assert.True(await WaitAsync(() => LoggerChannelForwarder.QueueSnapshotForTest.Dropped == 7, TimeSpan.FromSeconds(5)));
		Logger.Info(typeof(FakeSource), "工作器结束后的条目");

		var result = await LoggerChannelForwarder.StopAsync(TimeSpan.FromSeconds(5));

		Assert.True(result.Stopped);
		Assert.Equal(7, result.EntriesDropped);
		Assert.Equal(0, result.RemainingQueued);
		Assert.Equal(0, result.ActiveSends);
		Assert.False(LoggerChannelForwarder.Running);
	}

	[Fact]
	public void L07_DuplicateSubscription_EachHandleIsIndependent() {
		var count = 0;
		Action<LogEntry> handler = _ => Interlocked.Increment(ref count);
		var first = Logger.Subscribe(handler);
		var second = Logger.Subscribe(handler);
		try {
			first.Dispose();

			Logger.Info(typeof(FakeSource), "仍应转发");

			Assert.Equal(1, Volatile.Read(ref count));
		} finally {
			second.Dispose();
			first.Dispose();
		}
	}
}
