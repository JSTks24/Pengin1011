using System.Text;

namespace Pengin1011.Services.Discord;

public static class Mentions {
	public static string User(ulong userId) {
		return $"<@{userId}>";
	}

	public static string Role(ulong roleId) {
		return $"<@&{roleId}>";
	}

	public static string Channel(ulong channelId) {
		return $"<#{channelId}>";
	}

	public static string Timestamp(DateTimeOffset time, char style = 'F') {
		return $"<t:{time.ToUnixTimeSeconds()}:{style}>";
	}

	public static string StripUserMentions(string? text) {
		if (string.IsNullOrEmpty(text)) return "";

		var builder = new StringBuilder(text.Length);
		var i = 0;
		while (i < text.Length) {
			if (text[i] == '<' && i + 1 < text.Length && text[i + 1] == '@') {
				var j = i + 2;
				if (j < text.Length && text[j] == '!') j++;
				var digitsEnd = j;
				while (digitsEnd < text.Length && text[digitsEnd] >= '0' && text[digitsEnd] <= '9') digitsEnd++;
				if (digitsEnd > j && digitsEnd < text.Length && text[digitsEnd] == '>') {
					i = digitsEnd + 1;
					continue;
				}
			}
			builder.Append(text[i]);
			i++;
		}
		return builder.ToString().Trim();
	}
}
