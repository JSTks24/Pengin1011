namespace Pengin1011.Services.Discord;

public sealed record ForwarderChannelFailure(ulong ChannelId, int Count, string LastError);
