using System.Net;
using Discord;
using Discord.Net;
using QingQiu1011.Services.Discord;

namespace QingQiu1011.Tests;

public sealed class MessagesTests {
	[Fact]
	public async Task SendAsync_ShortText_SendsSingleMessage() {
		var channel = new FakeMessageChannel();

		var result = await Messages.SendAsync(channel, "hello");

		Assert.Equal(MessageSendStatus.Succeeded, result.Status);
		Assert.Equal(1, result.PlannedChunks);
		Assert.Single(result.Sent);
		var sent = Assert.Single(channel.Sent);
		Assert.Equal("hello", sent.Text);
		Assert.Equal("hello", result.First!.Content);
	}

	[Fact]
	public async Task SendAsync_LongText_SendsEachChunk() {
		var channel = new FakeMessageChannel();

		await Messages.SendAsync(channel, new string('a', 4000));

		Assert.Equal(3, channel.Sent.Count);
		Assert.All(channel.Sent, static sent => Assert.True(sent.Text!.Length <= 1900));
	}

	[Fact]
	public async Task SendAsync_DefaultsToAllowedMentionsNone() {
		var channel = new FakeMessageChannel();

		await Messages.SendAsync(channel, "hello");

		Assert.Same(AllowedMentions.None, channel.Sent[0].AllowedMentions);
	}

	[Fact]
	public async Task SendAsync_NullChannel_ReturnsNull() {
		var result = await Messages.SendAsync(null, "hello");

		Assert.Equal(MessageSendStatus.Failed, result.Status);
		Assert.Empty(result.Sent);
	}

	[Fact]
	public async Task SendAsync_EmptyText_ReturnsNullWithoutSending() {
		var channel = new FakeMessageChannel();

		var result = await Messages.SendAsync(channel, "");

		Assert.Equal(MessageSendStatus.Failed, result.Status);
		Assert.Empty(channel.Sent);
	}

	[Fact]
	public async Task SendAsync_ThrowingChannel_ReturnsNull() {
		var channel = new FakeMessageChannel { SendException = new InvalidOperationException() };

		var result = await Messages.SendAsync(channel, "hello");

		Assert.Equal(MessageSendStatus.Failed, result.Status);
	}

	[Fact]
	public async Task ReplyAsync_UsesReferenceToSource() {
		var channel = new FakeMessageChannel();
		var source = new FakeUserMessage { Id = 77, Channel = channel };

		await Messages.ReplyAsync(source, "answer");

		var sent = Assert.Single(channel.Sent);
		Assert.NotNull(sent.Reference);
		Assert.Equal(77UL, sent.Reference!.MessageId!.Value);
		Assert.Equal(channel.Id, sent.Reference.ChannelId);
		Assert.True(sent.Reference!.FailIfNotExists.IsSpecified);
		Assert.False(sent.Reference.FailIfNotExists.Value);
	}

	[Fact]
	public async Task ReplyAsync_Overflow_ContinuesWithoutReference() {
		var channel = new FakeMessageChannel();
		var source = new FakeUserMessage { Id = 77, Channel = channel };

		await Messages.ReplyAsync(source, new string('a', 4000));

		Assert.Equal(3, channel.Sent.Count);
		Assert.NotNull(channel.Sent[0].Reference);
		Assert.Null(channel.Sent[1].Reference);
		Assert.Null(channel.Sent[2].Reference);
	}

	[Fact]
	public async Task SendBulkAsync_RecordsPerChannelSuccess() {
		var ok1 = new FakeMessageChannel();
		var ok2 = new FakeMessageChannel();
		var bad = new FakeMessageChannel { SendException = new InvalidOperationException() };

		var results = await Messages.SendBulkAsync([ok1, bad, ok2], "notice", delayMs: 0);

		Assert.Equal(3, results.Count);
		Assert.True(results[0].Success);
		Assert.False(results[1].Success);
		Assert.True(results[2].Success);
		Assert.Same(ok2, results[2].Channel);
	}

	[Fact]
	public async Task EditAsync_TruncatesOverLimit() {
		var message = new FakeUserMessage { Content = "old" };

		var ok = await Messages.EditAsync(message, new string('a', 2500));

		Assert.True(ok);
		Assert.Equal(2000, message.Content.Length);
		Assert.Equal(1, message.Edits);
	}

	[Fact]
	public async Task EditAsync_NullMessage_ReturnsFalse() {
		var ok = await Messages.EditAsync(null, "text");

		Assert.False(ok);
	}

	[Fact]
	public async Task SendDirectAsync_Forbidden_ReturnsBlocked() {
		var user = new FakeUser { DmException = new HttpException(HttpStatusCode.Forbidden, null!, null, null, null) };

		var result = await Messages.SendDirectAsync(user, "处罚通知");

		Assert.Equal(DirectMessageResult.Blocked, result);
	}

	[Fact]
	public async Task SendDirectAsync_OtherException_ReturnsFailed() {
		var user = new FakeUser { DmException = new InvalidOperationException() };

		var result = await Messages.SendDirectAsync(user, "处罚通知");

		Assert.Equal(DirectMessageResult.Failed, result);
	}

	[Fact]
	public async Task SendDirectAsync_Deliverable_ReturnsSent() {
		var dm = new FakeDMChannel();
		var user = new FakeUser { DmChannel = dm };

		var result = await Messages.SendDirectAsync(user, "处罚通知");

		Assert.Equal(DirectMessageResult.Sent, result);
		var sent = Assert.Single(dm.Sent);
		Assert.Equal("处罚通知", sent.Text);
	}

	[Fact]
	public async Task SendDirectAsync_NullUser_ReturnsFailed() {
		var result = await Messages.SendDirectAsync(null, "text");

		Assert.Equal(DirectMessageResult.Failed, result);
	}

	[Fact]
	public async Task SendFileAsync_RecordsFilename() {
		var channel = new FakeMessageChannel();

		var message = await Messages.SendFileAsync(channel, [1, 2, 3], "log.txt", "日志");

		Assert.NotNull(message);
		var sent = Assert.Single(channel.Sent);
		Assert.Equal("log.txt", sent.Filename);
		Assert.Equal("日志", sent.Text);
	}

	[Fact]
	public async Task SendFileAsync_EmptyData_ReturnsNull() {
		var channel = new FakeMessageChannel();

		var message = await Messages.SendFileAsync(channel, [], "log.txt");

		Assert.Null(message);
		Assert.Empty(channel.Sent);
	}

	[Fact]
	public async Task SendAsync_FirstChunkFails_NothingSent() {
		var channel = new FakeMessageChannel { SendException = new InvalidOperationException() };

		var result = await Messages.SendAsync(channel, new string('a', 4000));

		Assert.Equal(MessageSendStatus.Failed, result.Status);
		Assert.Empty(result.Sent);
		Assert.Equal([0], result.FailedChunkIndexes);
		Assert.Empty(channel.Sent);
	}

	[Fact]
	public async Task SendAsync_SecondChunkFails_StopsAndReportsPartial() {
		var channel = new FakeMessageChannel();
		channel.FailOnCall.Add(2);

		var result = await Messages.SendAsync(channel, new string('a', 4000));

		Assert.Equal(MessageSendStatus.Partial, result.Status);
		Assert.Single(result.Sent);
		Assert.Equal([1], result.FailedChunkIndexes);
		Assert.Single(channel.Sent);
	}

	[Fact]
	public async Task SendDirectAsync_SecondChunkFails_ReturnsPartial() {
		var dm = new FakeDMChannel();
		dm.FailOnCall.Add(2);
		var user = new FakeUser { DmChannel = dm };

		var result = await Messages.SendDirectAsync(user, new string('a', 4000));

		Assert.Equal(DirectMessageResult.Partial, result);
		Assert.Single(dm.Sent);
	}

	[Fact]
	public async Task SendBulkAsync_PartialChannelResult_MapsSuccess() {
		var ok = new FakeMessageChannel();
		var bad = new FakeMessageChannel { SendException = new InvalidOperationException() };

		var results = await Messages.SendBulkAsync([bad, ok], "notice", delayMs: 0);

		Assert.False(results[0].Success);
		Assert.Equal(MessageSendStatus.Failed, results[0].Result.Status);
		Assert.True(results[1].Success);
	}
}
