using System.Net.Http;
using Pengin1011.Core;
using Discord;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;

namespace Pengin1011.Services.Discord;

public sealed record AttachmentData(byte[] Data, string MimeType, string Filename);

public static class Attachments {
	private static readonly HttpClient _http = new();

	private static readonly string[] _imageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

	public static async Task<AttachmentData?> DownloadAsync(IAttachment? attachment) {
		if (attachment == null) return null;
		try {
			var data = await _http.GetByteArrayAsync(attachment.Url);
			return new AttachmentData(data, attachment.ContentType ?? "", attachment.Filename);
		} catch (Exception e) {
			Logger.Error(typeof(Attachments), e, Loc.Format("AttachmentDownloadFailed", attachment.Url));
			return null;
		}
	}

	public static bool IsImage(string? contentType, string? filename) {
		if (!string.IsNullOrEmpty(contentType) && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return true;
		if (!string.IsNullOrEmpty(filename)) {
			var extension = Path.GetExtension(filename);
			foreach (var imageExtension in _imageExtensions) {
				if (string.Equals(extension, imageExtension, StringComparison.OrdinalIgnoreCase)) return true;
			}
		}
		return false;
	}
}
