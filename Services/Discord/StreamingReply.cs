using System.Text;
using Discord;
using Discord.Net;
using Pengin1011.Core.Logging;

namespace Pengin1011.Services.Discord;

public interface IReplySink {
	Task<IReplyPost?> ReplyAsync(string content, CancellationToken ct);
}

public interface IReplyPost {
	ulong Id { get; }
	Task<bool> EditAsync(string content, CancellationToken ct);
	Task<bool> DeleteAsync(CancellationToken ct);
}

public enum StreamSyncStatus {
	Unchanged,
	Updated,
	Incomplete,
}

public sealed class StreamingReplyOptions {
	public int Limit { get; init; } = MessageSplitter.DefaultLimit;
	public int EditIntervalMs { get; init; } = 3000;
}

public sealed class StreamingReply {
	private readonly IReplySink _sink;
	private readonly int _limit;
	private readonly int _editIntervalMs;
	private readonly TimeProvider _time;
	private readonly StringBuilder _buffer = new();
	private readonly List<IReplyPost> _posts = [];
	private readonly List<string> _postContents = [];
	private long _lastEdit;

	public IReadOnlyList<IReplyPost> Posts => _posts;

	public StreamingReply(IReplySink sink, StreamingReplyOptions? options = null, TimeProvider? time = null) {
		_sink = sink;
		_limit = options?.Limit ?? MessageSplitter.DefaultLimit;
		_editIntervalMs = options?.EditIntervalMs ?? 3000;
		_time = time ?? TimeProvider.System;
	}

	public void Append(string delta) {
		if (string.IsNullOrEmpty(delta)) return;
		_buffer.Append(delta);
	}

	public async Task<bool> StartAsync(string statusText, CancellationToken ct = default) {
		if (_posts.Count > 0 || string.IsNullOrEmpty(statusText)) return false;
		var post = await _sink.ReplyAsync(statusText, ct);
		if (post == null) return false;
		_posts.Add(post);
		_postContents.Add(statusText);
		return true;
	}

	public async Task<bool> SetStatusAsync(string statusText, CancellationToken ct = default) {
		if (_posts.Count == 0 || string.IsNullOrEmpty(statusText)) return false;
		if (_postContents[0] == statusText) return true;
		if (!await _posts[0].EditAsync(statusText, ct)) return false;
		_postContents[0] = statusText;
		_lastEdit = _time.GetTimestamp();
		return true;
	}

	public async Task<StreamSyncStatus> FlushAsync(CancellationToken ct = default) {
		if (_time.GetElapsedTime(_lastEdit).TotalMilliseconds < _editIntervalMs) return StreamSyncStatus.Unchanged;
		var chunks = MessageSplitter.Split(_buffer.ToString(), _limit);
		if (chunks.Count == 0) return StreamSyncStatus.Unchanged;
		var status = await SyncPostsAsync(chunks, ct);
		if (status == StreamSyncStatus.Updated) _lastEdit = _time.GetTimestamp();
		return status;
	}

	public async Task<StreamSyncStatus> FinalizeAsync(string fullText, CancellationToken ct = default) {
		if (string.IsNullOrEmpty(fullText)) return StreamSyncStatus.Unchanged;
		_buffer.Clear();
		_buffer.Append(fullText);
		var chunks = MessageSplitter.Split(fullText, _limit);
		var kept = await SyncPostsAsync(chunks, ct);
		if (kept == StreamSyncStatus.Incomplete) {
			return StreamSyncStatus.Incomplete;
		}
		var excessBefore = _posts.Count - chunks.Count;
		var removedAll = await RemoveTailPostsAsync(chunks.Count, ct);
		if (!removedAll) {
			return StreamSyncStatus.Incomplete;
		}
		if (kept == StreamSyncStatus.Unchanged && excessBefore <= 0) return StreamSyncStatus.Unchanged;
		if (kept == StreamSyncStatus.Updated) _lastEdit = _time.GetTimestamp();
		return StreamSyncStatus.Updated;
	}

	public async Task<bool> PublishErrorAsync(string errorText, CancellationToken ct = default) {
		if (string.IsNullOrEmpty(errorText)) return false;
		if (_posts.Count == 0) {
			var post = await _sink.ReplyAsync(errorText, ct);
			if (post == null) return false;
			_posts.Add(post);
			_postContents.Add(errorText);
			return true;
		}
		if (_postContents[0] == errorText) return true;
		if (!await _posts[0].EditAsync(errorText, ct)) return false;
		_postContents[0] = errorText;
		_lastEdit = _time.GetTimestamp();
		return true;
	}

	private async Task<StreamSyncStatus> SyncPostsAsync(IReadOnlyList<string> chunks, CancellationToken ct) {
		var updated = false;
		for (var i = 0; i < chunks.Count; i++) {
			if (i < _posts.Count) {
				if (_postContents[i] == chunks[i]) continue;
				if (!await _posts[i].EditAsync(chunks[i], ct)) {
					return StreamSyncStatus.Incomplete;
				}
				_postContents[i] = chunks[i];
				updated = true;
			} else {
				var post = await _sink.ReplyAsync(chunks[i], ct);
				if (post == null) {
					return StreamSyncStatus.Incomplete;
				}
				_posts.Add(post);
				_postContents.Add(chunks[i]);
				updated = true;
			}
		}
		return updated ? StreamSyncStatus.Updated : StreamSyncStatus.Unchanged;
	}

	private async Task<bool> RemoveTailPostsAsync(int keepCount, CancellationToken ct) {
		while (_posts.Count > keepCount) {
			var index = _posts.Count - 1;
			var post = _posts[index];
			bool removed;
			try {
				removed = await post.DeleteAsync(ct);
			} catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound) {
				removed = true;
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception e) {
				Logger.Error(typeof(StreamingReply), e, $"删除流式余帖失败：post={post.Id}");
				removed = false;
			}
			if (!removed) {
				return false;
			}
			_posts.RemoveAt(index);
			_postContents.RemoveAt(index);
		}
		return true;
	}
}

public static class ReplySinks {
	public static IReplySink FromMessage(IMessage source) {
		return new MessageReplySink(source);
	}
}

internal sealed class MessageReplySink : IReplySink {
	private readonly IMessageChannel? _channel;

	public MessageReplySink(IMessage source) {
		_channel = source.Channel as IMessageChannel;
	}

	public async Task<IReplyPost?> ReplyAsync(string content, CancellationToken ct) {
		var result = await Messages.SendAsync(_channel, content, ct: ct);
		if (!result.Success) {
			if (result.Status == MessageSendStatus.Partial) {
				Logger.Error(typeof(StreamingReply), $"流式回复部分送达（{result.Sent.Count}/{result.PlannedChunks} 片），不能作为单帖继续");
			}
			return null;
		}
		return new MessageReplyPost(result.First!);
	}
}

internal sealed class MessageReplyPost : IReplyPost {
	private readonly IUserMessage _message;

	public MessageReplyPost(IUserMessage message) {
		_message = message;
	}

	public ulong Id => _message.Id;

	public Task<bool> EditAsync(string content, CancellationToken ct) {
		return Messages.EditAsync(_message, content, ct);
	}

	public Task<bool> DeleteAsync(CancellationToken ct) {
		return Messages.DeleteAsync(_message, ct);
	}
}
