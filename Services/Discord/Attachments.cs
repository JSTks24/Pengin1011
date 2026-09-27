using System.Net.Http;
using Discord;
using QingQiu1011.Core.Logging;

namespace QingQiu1011.Services.Discord;

public sealed record AttachmentData(byte[] Data, string MimeType, string Filename);

public static class Attachments {
	private static readonly HttpClient Http = new();

	private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

	public static async Task<AttachmentData?> DownloadAsync(IAttachment? attachment) {
		if (attachment == null) return null;
		try {
			var data = await Http.GetByteArrayAsync(attachment.Url);
			return new AttachmentData(data, attachment.ContentType ?? "", attachment.Filename);
		} catch (Exception e) {
			Logger.Error(typeof(Attachments), e, $"下载附件失败：{attachment.Url}");
			return null;
		}
	}

	public static bool IsImage(string? contentType, string? filename) {
		if (!string.IsNullOrEmpty(contentType) && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return true;
		if (!string.IsNullOrEmpty(filename)) {
			var extension = Path.GetExtension(filename);
			foreach (var imageExtension in ImageExtensions) {
				if (string.Equals(extension, imageExtension, StringComparison.OrdinalIgnoreCase)) return true;
			}
		}
		return false;
	}
}
