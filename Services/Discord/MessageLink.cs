using System.Globalization;

namespace Pengin1011.Services.Discord;

public sealed record MessageLinkParts(ulong GuildId, ulong ChannelId, ulong MessageId);

public static class MessageLink {
	public static bool TryParse(string? input, out MessageLinkParts? parts) {
		parts = null;
		if (string.IsNullOrWhiteSpace(input)) return false;
		if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)) return false;
		if (uri.Scheme != Uri.UriSchemeHttps) return false;
		if (!IsDiscordHost(uri.Host)) return false;

		var segments = uri.GetComponents(UriComponents.Path, UriFormat.Unescaped).Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length < 4 || segments[0] != "channels") return false;
		if (!ulong.TryParse(segments[1], CultureInfo.InvariantCulture, out var guildId)) return false;
		if (!ulong.TryParse(segments[2], CultureInfo.InvariantCulture, out var channelId)) return false;
		if (!ulong.TryParse(segments[3], CultureInfo.InvariantCulture, out var messageId)) return false;

		parts = new MessageLinkParts(guildId, channelId, messageId);
		return true;
	}

	private static bool IsDiscordHost(string host) {
		return host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
			|| host.Equals("ptb.discord.com", StringComparison.OrdinalIgnoreCase)
			|| host.Equals("canary.discord.com", StringComparison.OrdinalIgnoreCase)
			|| host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase);
	}
}
