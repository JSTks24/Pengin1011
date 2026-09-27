using Discord;
using QingQiu1011.Core.Logging;

namespace QingQiu1011.Services.Discord;

public static class History {
	public const int BatchSize = 100;
	public const int BatchPauseMs = 2000;

	public static async Task<IReadOnlyList<IMessage>> ReadAsync(IMessageChannel? channel, int limit, ulong? beforeId = null, ulong? afterId = null, bool oldestFirst = false, int batchPauseMs = BatchPauseMs, CancellationToken ct = default) {
		var messages = new List<IMessage>();
		if (channel == null || limit <= 0) return messages;

		var after = afterId != null;
		var direction = after ? Direction.After : Direction.Before;
		var cursor = afterId ?? beforeId;
		var remaining = limit;
		var batches = new List<IReadOnlyCollection<IMessage>>();

		while (remaining > 0) {
			if (ct.IsCancellationRequested) break;
			var take = Math.Min(remaining, BatchSize);
			List<IMessage> batch;
			try {
				batch = cursor != null
					? (await channel.GetMessagesAsync(cursor.Value, direction, take).FlattenAsync()).ToList()
					: (await channel.GetMessagesAsync(take).FlattenAsync()).ToList();
			} catch (Exception e) {
				Logger.Error(typeof(History), e, $"读取历史消息批次失败：channel={channel.Id} cursor={cursor}");
				break;
			}
			if (batch.Count == 0) break;

			batches.Add(batch);
			remaining -= batch.Count;
			cursor = after
				? batch.Max(static message => message.Id)
				: batch.Min(static message => message.Id);

			if (remaining > 0 && batchPauseMs > 0) {
				try {
					await Task.Delay(batchPauseMs, ct);
				} catch (OperationCanceledException) {
					break;
				}
			}
		}

		foreach (var batch in batches) {
			if (after) messages.AddRange(batch.Reverse()); else messages.AddRange(batch);
		}
		if (after ? !oldestFirst : oldestFirst) messages.Reverse();
		return messages;
	}
}
