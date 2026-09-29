using Discord;
using Pengin1011.Core.Logging;

namespace Pengin1011.Services.Discord;

public static class Interactions {
	public static async Task<bool> DeferAsync(IDiscordInteraction? interaction, bool ephemeral = true) {
		if (interaction == null) return false;
		if (interaction.HasResponded) return true;
		try {
			await interaction.DeferAsync(ephemeral);
			return true;
		} catch (Exception e) {
			Logger.Error(typeof(Interactions), e, "Defer 交互失败");
			return false;
		}
	}

	public static async Task<IUserMessage?> FollowupAsync(IDiscordInteraction? interaction, string? text, bool ephemeral = true) {
		if (interaction == null || string.IsNullOrEmpty(text)) return null;
		try {
			return await interaction.FollowupAsync(text, ephemeral: ephemeral, allowedMentions: AllowedMentions.None);
		} catch (Exception e) {
			Logger.Error(typeof(Interactions), e, "Followup 交互失败");
			return null;
		}
	}

	public static async Task<IUserMessage?> FollowupFileAsync(IDiscordInteraction? interaction, byte[]? data, string filename, string? text = null, bool ephemeral = true) {
		if (interaction == null || data == null || data.Length == 0 || string.IsNullOrEmpty(filename)) return null;
		try {
			using var stream = new MemoryStream(data);
			return await interaction.FollowupWithFileAsync(stream, filename, text, ephemeral: ephemeral);
		} catch (Exception e) {
			Logger.Error(typeof(Interactions), e, $"Followup 交互文件失败：{filename}");
			return null;
		}
	}

	public static async Task<bool> EditOriginalAsync(IDiscordInteraction? interaction, string? text) {
		if (interaction == null) return false;
		try {
			await interaction.ModifyOriginalResponseAsync(properties => properties.Content = text ?? "");
			return true;
		} catch (Exception e) {
			Logger.Error(typeof(Interactions), e, "编辑交互原始响应失败");
			return false;
		}
	}
}
