using System.Net.Http;
using Pengin1011.Core;
using Discord;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;

namespace Pengin1011.Services.Discord;

public sealed record AttachmentData(byte[] Data, string MimeType, string Filename);

public static class Attachments {
	public const int DefaultMaxBytes = 25 * 1024 * 1024;

	private static readonly HttpClient _http = new();

	private static readonly string[] _imageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

	public static Task<AttachmentData?> DownloadAsync(IAttachment? attachment) {
		return DownloadAsync(attachment, CancellationToken.None);
	}

	public static Task<AttachmentData?> DownloadAsync(IAttachment? attachment, CancellationToken ct, int maxBytes = DefaultMaxBytes) {
		return DownloadAsync(attachment, _http, ct, maxBytes);
	}

	internal static async Task<AttachmentData?> DownloadAsync(IAttachment? attachment, HttpClient client, CancellationToken ct, int maxBytes = DefaultMaxBytes) {
		if (attachment == null || maxBytes <= 0) return null;
		ct.ThrowIfCancellationRequested();
		try {
			if (attachment.Size > maxBytes) throw new InvalidDataException(Loc.Format("AttachmentSizeExceeded", maxBytes));
			using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
			if (client.Timeout != Timeout.InfiniteTimeSpan) budget.CancelAfter(client.Timeout);
			using var response = await client.GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead, budget.Token);
			response.EnsureSuccessStatusCode();
			if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException(Loc.Format("AttachmentSizeExceeded", maxBytes));
			await using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
			using var data = new MemoryStream();
			var buffer = new byte[Math.Min(81920, maxBytes)];
			while (true) {
				var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maxBytes - data.Length + 1)), budget.Token);
				if (read == 0) break;
				if (data.Length + read > maxBytes) throw new InvalidDataException(Loc.Format("AttachmentSizeExceeded", maxBytes));
				data.Write(buffer, 0, read);
			}
			return new AttachmentData(data.ToArray(), attachment.ContentType ?? "", attachment.Filename);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
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
