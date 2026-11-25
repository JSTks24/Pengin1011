namespace Pengin1011.Services.Discord;

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
