namespace Pengin1011.Services.AI;

public sealed record AIStreamDelta(string Text, AIUsage? Usage = null);
