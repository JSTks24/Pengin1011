using Discord;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

public sealed class HistoryTests {
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ReadAsync_RequestReceivesCancellationTokenAndReturnsCollectedMessages(bool withCursor) {
		using var source = new CancellationTokenSource();
		var channel = new FakeMessageChannel();
		var message = new FakeUserMessage { Id = 5 };
		channel.ReadHistory = options => ReadThenCancelAsync(message, source, options);

		var result = await History.ReadAsync(channel, 2, beforeId: withCursor ? 10UL : null, batchPauseMs: 0, ct: source.Token);

		Assert.Same(message, Assert.Single(result));
		Assert.Equal(source.Token, Assert.Single(channel.HistoryRequests)!.CancelToken);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ReadAsync_CancelDuringRequest_ReturnsWithoutLoggingFailure(bool withCursor) {
		using var source = new CancellationTokenSource();
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var channel = new FakeMessageChannel {
			ReadHistory = options => ReadUntilCanceledAsync(started, options),
		};
		var read = History.ReadAsync(channel, 2, beforeId: withCursor ? 10UL : null, ct: source.Token);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		source.Cancel();

		Assert.Empty(await read.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.Equal(source.Token, Assert.Single(channel.HistoryRequests)!.CancelToken);
	}

	private static async IAsyncEnumerable<IReadOnlyCollection<IMessage>> ReadThenCancelAsync(IMessage message, CancellationTokenSource source, RequestOptions? options) {
		await Task.CompletedTask;
		options!.CancelToken.ThrowIfCancellationRequested();
		yield return new[] { message };
		source.Cancel();
	}

	private static async IAsyncEnumerable<IReadOnlyCollection<IMessage>> ReadUntilCanceledAsync(TaskCompletionSource started, RequestOptions? options) {
		started.TrySetResult();
		await Task.Delay(Timeout.InfiniteTimeSpan, options!.CancelToken);
		yield break;
	}
}
