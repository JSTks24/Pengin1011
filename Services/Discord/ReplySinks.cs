using Discord;

namespace Pengin1011.Services.Discord;

public static class ReplySinks {
	public static IReplySink FromMessage(IMessage source) {
		return new MessageReplySink(source);
	}
}
