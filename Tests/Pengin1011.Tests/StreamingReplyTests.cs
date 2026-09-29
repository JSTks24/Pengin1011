using Discord;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

public sealed class StreamingReplyTests {
	private readonly FakeReplySink _sink = new();
	private readonly FakeTimeProvider _time = new();

	private StreamingReply MakeSession(StreamingReplyOptions? options = null) {
		return new StreamingReply(_sink, options, _time);
	}

	[Fact]
	public async Task StartAsync_CreatesStatusPost() {
		var session = MakeSession();

		var ok = await session.StartAsync("思考中");

		Assert.True(ok);
		var post = Assert.Single(_sink.Posts);
		Assert.Equal("思考中", post.Content);
	}

	[Fact]
	public async Task StartAsync_SecondCall_ReturnsFalse() {
		var session = MakeSession();
		await session.StartAsync("思考中");

		var ok = await session.StartAsync("再次");

		Assert.False(ok);
		Assert.Single(_sink.Posts);
	}

	[Fact]
	public async Task StartAsync_EmptyText_ReturnsFalse() {
		var session = MakeSession();

		var ok = await session.StartAsync("");

		Assert.False(ok);
		Assert.Empty(_sink.Posts);
	}

	[Fact]
	public async Task FlushAsync_NoContent_ReturnsUnchanged() {
		var session = MakeSession();
		await session.StartAsync("思考中");

		var status = await session.FlushAsync();

		Assert.Equal(StreamSyncStatus.Unchanged, status);
	}

	[Fact]
	public async Task FlushAsync_WithContent_CreatesFirstPostWithoutStart() {
		var session = MakeSession();
		session.Append("hello");

		var status = await session.FlushAsync();

		Assert.Equal(StreamSyncStatus.Updated, status);
		var post = Assert.Single(_sink.Posts);
		Assert.Equal("hello", post.Content);
	}

	[Fact]
	public async Task FlushAsync_WithinThrottleWindow_SkipsEdit() {
		var session = MakeSession();
		session.Append("hello");
		await session.FlushAsync();

		session.Append(" world");
		_time.AdvanceMs(1000);
		var status = await session.FlushAsync();

		Assert.Equal(StreamSyncStatus.Unchanged, status);
		Assert.Equal("hello", _sink.Posts[0].Content);
	}

	[Fact]
	public async Task FlushAsync_AfterThrottleWindow_Edits() {
		var session = MakeSession();
		session.Append("hello");
		await session.FlushAsync();

		session.Append(" world");
		_time.AdvanceMs(3000);
		var status = await session.FlushAsync();

		Assert.Equal(StreamSyncStatus.Updated, status);
		Assert.Equal("hello world", _sink.Posts[0].Content);
	}

	[Fact]
	public async Task FlushAsync_UnchangedContent_DoesNotEdit() {
		var session = MakeSession();
		session.Append("hello");
		await session.FlushAsync();

		_time.AdvanceMs(3000);
		var status = await session.FlushAsync();

		Assert.Equal(StreamSyncStatus.Unchanged, status);
		Assert.Equal(0, _sink.Posts[0].Edits);
	}

	[Fact]
	public async Task FlushAsync_Overflow_CreatesContinuationPosts() {
		var session = MakeSession();
		session.Append(new string('a', 4500));

		var status = await session.FlushAsync();

		Assert.Equal(StreamSyncStatus.Updated, status);
		Assert.Equal(3, _sink.Posts.Count);
		Assert.Equal(new string('a', 1900), _sink.Posts[0].Content);
		Assert.Equal(new string('a', 1900), _sink.Posts[1].Content);
		Assert.Equal(new string('a', 700), _sink.Posts[2].Content);
	}

	[Fact]
	public async Task FlushAsync_GrowingOverflow_EditsOnlyChangedPost() {
		var session = MakeSession();
		session.Append(new string('a', 4500));
		await session.FlushAsync();

		session.Append(new string('b', 100));
		_time.AdvanceMs(3000);
		var status = await session.FlushAsync();

		Assert.Equal(StreamSyncStatus.Updated, status);
		Assert.Equal(3, _sink.Posts.Count);
		Assert.Equal(0, _sink.Posts[0].Edits);
		Assert.Equal(0, _sink.Posts[1].Edits);
		Assert.Equal(new string('a', 700) + new string('b', 100), _sink.Posts[2].Content);
	}

	[Fact]
	public async Task FinalizeAsync_BypassesThrottle() {
		var session = MakeSession();
		await session.StartAsync("思考中");
		session.Append("partial");
		await session.FlushAsync();

		_time.AdvanceMs(100);
		var status = await session.FinalizeAsync("final answer");

		Assert.Equal(StreamSyncStatus.Updated, status);
		Assert.Equal("final answer", _sink.Posts[0].Content);
	}

	[Fact]
	public async Task FinalizeAsync_WithoutStart_CreatesPosts() {
		var session = MakeSession();

		var status = await session.FinalizeAsync("answer");

		Assert.Equal(StreamSyncStatus.Updated, status);
		var post = Assert.Single(_sink.Posts);
		Assert.Equal("answer", post.Content);
	}

	[Fact]
	public async Task FinalizeAsync_EmptyText_ReturnsUnchanged() {
		var session = MakeSession();

		var status = await session.FinalizeAsync("");

		Assert.Equal(StreamSyncStatus.Unchanged, status);
		Assert.Empty(_sink.Posts);
	}

	[Fact]
	public async Task FinalizeAsync_ShrinkFromThreeToOne_DeletesTailPosts() {
		var session = MakeSession();
		session.Append(new string('a', 4500));
		await session.FlushAsync();
		var postsBefore = _sink.Posts.Select(post => post.Id).ToList();

		var status = await session.FinalizeAsync("short");

		Assert.Equal(StreamSyncStatus.Updated, status);
		Assert.Single(session.Posts);
		Assert.Equal("short", _sink.Posts[0].Content);
		Assert.Equal([postsBefore[2], postsBefore[1]], _sink.Deletions);
	}

	[Fact]
	public async Task FinalizeAsync_ShrinkFromThreeToTwo_DeletesOnlyLast() {
		var session = MakeSession();
		session.Append(new string('a', 4500));
		await session.FlushAsync();
		var postsBefore = _sink.Posts.Select(post => post.Id).ToList();

		var status = await session.FinalizeAsync(new string('a', 2500));

		Assert.Equal(StreamSyncStatus.Updated, status);
		Assert.Equal(2, session.Posts.Count);
		Assert.Equal([postsBefore[2]], _sink.Deletions);
	}

	[Fact]
	public async Task FinalizeAsync_DeleteFails_ReturnsIncompleteAndRetainsForRetry() {
		var session = MakeSession();
		session.Append(new string('a', 4500));
		await session.FlushAsync();
		_sink.Posts[2].DeleteFails = true;

		var status = await session.FinalizeAsync("short");

		Assert.Equal(StreamSyncStatus.Incomplete, status);
		Assert.Equal(3, session.Posts.Count);
		Assert.Empty(_sink.Deletions);

		_sink.Posts[2].DeleteFails = false;
		status = await session.FinalizeAsync("short");

		Assert.Equal(StreamSyncStatus.Updated, status);
		Assert.Single(session.Posts);
	}

	[Fact]
	public async Task FinalizeAsync_DeleteNotFound_TreatedAsDone() {
		var session = MakeSession();
		session.Append(new string('a', 4500));
		await session.FlushAsync();
		_sink.Posts[2].DeleteNotFound = true;
		_sink.Posts[1].DeleteNotFound = true;

		var status = await session.FinalizeAsync("short");

		Assert.Equal(StreamSyncStatus.Updated, status);
		Assert.Single(session.Posts);
	}

	[Fact]
	public async Task FinalizeAsync_KeptPrefixEditFails_DoesNotDeleteTail() {
		var session = MakeSession();
		session.Append(new string('a', 4500));
		await session.FlushAsync();
		_sink.Posts[0].EditFails = true;

		var status = await session.FinalizeAsync(new string('b', 4500));

		Assert.Equal(StreamSyncStatus.Incomplete, status);
		Assert.Equal(3, session.Posts.Count);
		Assert.Empty(_sink.Deletions);
	}

	[Fact]
	public async Task SetStatusAsync_EditsFirstPostImmediately() {
		var session = MakeSession();
		await session.StartAsync("思考中");
		session.Append("hello");
		await session.FlushAsync();

		var ok = await session.SetStatusAsync("查询中");

		Assert.True(ok);
		Assert.Equal("查询中", _sink.Posts[0].Content);
	}

	[Fact]
	public async Task SetStatusAsync_BeforeStart_ReturnsFalse() {
		var session = MakeSession();

		var ok = await session.SetStatusAsync("查询中");

		Assert.False(ok);
		Assert.Empty(_sink.Posts);
	}

	[Fact]
	public async Task PublishErrorAsync_WithoutPosts_CreatesPost() {
		var session = MakeSession();

		var ok = await session.PublishErrorAsync("出错了");

		Assert.True(ok);
		var post = Assert.Single(_sink.Posts);
		Assert.Equal("出错了", post.Content);
	}

	[Fact]
	public async Task PublishErrorAsync_WithPosts_EditsFirstPost() {
		var session = MakeSession();
		await session.StartAsync("思考中");

		var ok = await session.PublishErrorAsync("出错了");

		Assert.True(ok);
		Assert.Equal("出错了", _sink.Posts[0].Content);
	}

	[Fact]
	public async Task Cancel_PropagatesToRealMessageAdapter() {
		var message = new FakeUserMessage { Content = "old" };
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Messages.EditAsync(message, "new", cts.Token));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Messages.DeleteAsync(message, cts.Token));
	}

	[Fact]
	public async Task CustomOptions_SmallerLimit() {
		var session = MakeSession(new StreamingReplyOptions { Limit = 10 });
		session.Append(new string('x', 25));

		await session.FlushAsync();

		Assert.Equal(3, _sink.Posts.Count);
	}

	private sealed class FakeReplySink : IReplySink {
		public List<FakeReplyPost> Posts { get; } = [];
		public List<ulong> Deletions { get; } = [];

		public Task<IReplyPost?> ReplyAsync(string content, CancellationToken ct) {
			var post = new FakeReplyPost((ulong)(Posts.Count + 1), content, Deletions);
			Posts.Add(post);
			return Task.FromResult<IReplyPost?>(post);
		}
	}

	private sealed class FakeReplyPost : IReplyPost {
		private readonly List<ulong> _deletions;

		public FakeReplyPost(ulong id, string content, List<ulong> deletions) {
			Id = id;
			Content = content;
			_deletions = deletions;
		}

		public int Edits;
		public bool EditFails;
		public bool DeleteFails;
		public bool DeleteNotFound;

		public ulong Id { get; }
		public string Content { get; private set; }

		public Task<bool> EditAsync(string content, CancellationToken ct) {
			if (EditFails) return Task.FromResult(false);
			Content = content;
			Edits++;
			return Task.FromResult(true);
		}

		public Task<bool> DeleteAsync(CancellationToken ct) {
			if (DeleteFails) return Task.FromResult(false);
			_deletions.Add(Id);
			return Task.FromResult(true);
		}
	}

	private sealed class FakeTimeProvider : TimeProvider {
		private long _timestamp = 1_000_000_000_000;

		public override long GetTimestamp() => _timestamp;

		public void AdvanceMs(int milliseconds) {
			_timestamp += milliseconds * TimestampFrequency / 1000;
		}
	}
}
