using Discord;

namespace Pengin1011.Tests;

public sealed record FakeSent(ulong Id, string? Text, AllowedMentions? AllowedMentions, MessageReference? Reference, string? Filename = null);
