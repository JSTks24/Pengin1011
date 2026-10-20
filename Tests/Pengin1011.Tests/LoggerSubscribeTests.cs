using Pengin1011.Core.Logging;

namespace Pengin1011.Tests;

public sealed class LoggerSubscribeTests {
	private sealed class FakeSource;

	private static string TempLog(string fileName) {
		return Path.Combine(Path.GetTempPath(), $"qq1011_{Guid.NewGuid():N}", "logs", fileName);
	}

	private static void Cleanup(string path) {
		Logger.SetLogPathForTest(null);
		Logger.SetInfoPathForTest(null);
		try { Directory.Delete(Path.GetDirectoryName(Path.GetFullPath(path))!, true); } catch (IOException) { }
	}

	[Fact]
	public void Subscriber_ReceivesEveryLevel() {
		var errorPath = TempLog("error.log");
		var infoPath = TempLog("info.log");
		Logger.SetLogPathForTest(errorPath);
		Logger.SetInfoPathForTest(infoPath);
		try {
			var received = new List<LogEntry>();
			using var subscription = Logger.Subscribe(received.Add);
			Logger.Info(typeof(FakeSource), "普通");
			Logger.Warn(typeof(FakeSource), "警告");
			Logger.Error(typeof(FakeSource), "错误");
			Assert.Equal([LogLevel.Info, LogLevel.Warn, LogLevel.Error], received.Select(entry => entry.Level));
			Assert.All(received, entry => Assert.Equal(typeof(FakeSource), entry.Source));
		} finally {
			Cleanup(errorPath);
		}
	}

	[Fact]
	public void Subscriber_ReceivesExceptionPayload() {
		var errorPath = TempLog("error.log");
		Logger.SetLogPathForTest(errorPath);
		try {
			LogEntry? received = null;
			using var subscription = Logger.Subscribe(entry => received = entry);
			var exception = new InvalidOperationException("炸了");
			Logger.Error(typeof(FakeSource), exception, "操作失败");
			Assert.NotNull(received);
			Assert.Same(exception, received!.Exception);
			Assert.Equal("操作失败", received.Message);
			Assert.Equal(LogLevel.Error, received.Level);
		} finally {
			Cleanup(errorPath);
		}
	}

	[Fact]
	public void Subscriber_Throwing_DoesNotBreakOthersOrCaller() {
		var errorPath = TempLog("error.log");
		Logger.SetLogPathForTest(errorPath);
		try {
			var received = new List<LogEntry>();
			using var first = Logger.Subscribe(_ => throw new InvalidOperationException("订阅者炸了"));
			using var second = Logger.Subscribe(received.Add);
			Logger.Error(typeof(FakeSource), "不受影响");
			Assert.Single(received);
		} finally {
			Cleanup(errorPath);
		}
	}

	[Fact]
	public void Dispose_StopsDelivery() {
		var infoPath = TempLog("info.log");
		Logger.SetInfoPathForTest(infoPath);
		try {
			var received = new List<LogEntry>();
			var subscription = Logger.Subscribe(received.Add);
			Logger.Info(typeof(FakeSource), "第一条");
			subscription.Dispose();
			Logger.Info(typeof(FakeSource), "第二条");
			Assert.Single(received);
		} finally {
			Cleanup(infoPath);
		}
	}

	[Fact]
	public void Dispose_IsIdempotent() {
		var errorPath = TempLog("error.log");
		Logger.SetLogPathForTest(errorPath);
		try {
			var received = new List<LogEntry>();
			var subscription = Logger.Subscribe(received.Add);
			subscription.Dispose();
			subscription.Dispose();
			Logger.Error(typeof(FakeSource), "已退订");
			Assert.Empty(received);
		} finally {
			Cleanup(errorPath);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Subscriber_NestedLogging_WritesLocallyWithoutRepublishing(bool dispatchOnTask) {
		var infoPath = TempLog("info.log");
		Logger.SetInfoPathForTest(infoPath);
		try {
			var calls = 0;
			var received = new List<LogEntry>();
			using var first = Logger.Subscribe(_ => {
				if (++calls > 2) return;
				if (dispatchOnTask) Task.Run(() => Logger.Info(typeof(FakeSource), "嵌套")).GetAwaiter().GetResult();
				else Logger.Info(typeof(FakeSource), "嵌套");
			});
			using var second = Logger.Subscribe(received.Add);

			Logger.Info(typeof(FakeSource), "外层");

			Assert.Equal(1, calls);
			Assert.Equal("外层", Assert.Single(received).Message);
			var content = File.ReadAllText(infoPath);
			Assert.Contains("外层", content);
			Assert.Contains("嵌套", content);
		} finally {
			Cleanup(infoPath);
		}
	}

	[Fact]
	public async Task Subscriber_DeferredChildLoggingAfterPublish_ReceivesItsOwnDelivery() {
		var infoPath = TempLog("info.log");
		Logger.SetInfoPathForTest(infoPath);
		try {
			var continueLogging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			Task? child = null;
			var received = new List<LogEntry>();
			using var subscription = Logger.Subscribe(entry => {
				received.Add(entry);
				if (entry.Message != "外层") return;
				child = Task.Run(async () => {
					await continueLogging.Task;
					Logger.Info(typeof(FakeSource), "后续");
				});
			});
			Logger.Info(typeof(FakeSource), "外层");

			continueLogging.SetResult();
			await child!.WaitAsync(TimeSpan.FromSeconds(5));

			Assert.Equal(["外层", "后续"], received.Select(entry => entry.Message));
		} finally {
			Cleanup(infoPath);
		}
	}
}
