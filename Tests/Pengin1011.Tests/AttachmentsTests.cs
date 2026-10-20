using System.Net;
using System.Reflection;
using Discord;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

public sealed class AttachmentsTests {
	[Theory]
	[InlineData("image/png", "file.bin", true)]
	[InlineData("image/jpeg", "file.bin", true)]
	[InlineData("IMAGE/GIF", "file.bin", true)]
	[InlineData("text/plain", "file.txt", false)]
	[InlineData("application/octet-stream", "photo.png", true)]
	[InlineData(null, "photo.JPG", true)]
	[InlineData(null, "photo.webp", true)]
	[InlineData(null, "data.txt", false)]
	[InlineData(null, "noext", false)]
	[InlineData("text/plain", "data.txt", false)]
	[InlineData(null, null, false)]
	public void IsImage_VariousInputs_ReturnsExpected(string? contentType, string? filename, bool expected) {
		var result = Attachments.IsImage(contentType, filename);

		Assert.Equal(expected, result);
	}

	[Fact]
	public async Task DownloadAsync_Null_ReturnsNull() {
		var data = await Attachments.DownloadAsync(null);

		Assert.Null(data);
	}

	[Fact]
	public async Task DownloadAsync_DeclaredAttachmentTooLarge_DoesNotRequest() {
		using var handler = new FakeHandler(_ => Task.FromResult(Response(new byte[1])));
		using var client = new HttpClient(handler);

		var data = await Attachments.DownloadAsync(Attachment(9), client, default, 8);

		Assert.Null(data);
		Assert.Equal(0, handler.Calls);
	}

	[Fact]
	public async Task DownloadAsync_DeclaredResponseTooLarge_DoesNotReadBody() {
		using var response = Response(new byte[9]);
		using var handler = new FakeHandler(_ => Task.FromResult(response));
		using var client = new HttpClient(handler);

		var data = await Attachments.DownloadAsync(Attachment(1), client, default, 8);

		Assert.Null(data);
	}

	[Theory]
	[InlineData(null)]
	[InlineData(1L)]
	public async Task DownloadAsync_ActualBodyExceedsBudget_ReturnsNull(long? declaredLength) {
		using var response = Response(new byte[9], declaredLength);
		using var handler = new FakeHandler(_ => Task.FromResult(response));
		using var client = new HttpClient(handler);

		var data = await Attachments.DownloadAsync(Attachment(1), client, default, 8);

		Assert.Null(data);
	}

	[Fact]
	public async Task DownloadAsync_UnknownLengthAtBudget_ReturnsDataAndMetadata() {
		var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
		using var handler = new FakeHandler(_ => Task.FromResult(Response(bytes, null)));
		using var client = new HttpClient(handler);

		var data = await Attachments.DownloadAsync(Attachment(1), client, default, 8);

		Assert.NotNull(data);
		Assert.Equal(bytes, data.Data);
		Assert.Equal("application/octet-stream", data.MimeType);
		Assert.Equal("file.bin", data.Filename);
	}

	[Fact]
	public async Task DownloadAsync_CancelDuringBody_PropagatesCancellation() {
		using var source = new CancellationTokenSource();
		using var stream = new BlockingStream();
		using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(stream) };
		using var handler = new FakeHandler(_ => Task.FromResult(response));
		using var client = new HttpClient(handler);
		var download = Attachments.DownloadAsync(Attachment(1), client, source.Token, 8);
		await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		source.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
	}

	[Fact]
	public async Task DownloadAsync_BodyTimeout_ReturnsNull() {
		using var stream = new BlockingStream();
		using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(stream) };
		using var handler = new FakeHandler(_ => Task.FromResult(response));
		using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };

		var data = await Attachments.DownloadAsync(Attachment(1), client, default, 8).WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Null(data);
	}

	[Fact]
	public async Task DownloadAsync_CanceledBeforeRequest_PropagatesCancellation() {
		using var source = new CancellationTokenSource();
		source.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Attachments.DownloadAsync(Attachment(1), source.Token));
	}

	private static IAttachment Attachment(int size) {
		var result = DispatchProxy.Create<IAttachment, AttachmentProxy>();
		((AttachmentProxy)(object)result).Size = size;
		return result;
	}

	private static HttpResponseMessage Response(byte[] bytes, long? declaredLength = -1) {
		var content = new UnknownLengthContent(new MemoryStream(bytes));
		if (declaredLength != null) content.Headers.ContentLength = declaredLength < 0 ? bytes.Length : declaredLength;
		return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
	}

	public class AttachmentProxy : DispatchProxy {
		public int Size { get; set; }

		protected override object? Invoke(MethodInfo? method, object?[]? arguments) {
			return method?.Name switch {
				"get_Size" => Size,
				"get_Url" => "https://cdn.example.com/file.bin",
				"get_Filename" => "file.bin",
				"get_ContentType" => "application/octet-stream",
				_ => throw new NotSupportedException(method?.Name),
			};
		}
	}

	private sealed class FakeHandler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler {
		public int Calls { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
			Calls++;
			return send(ct);
		}
	}

	private sealed class UnknownLengthContent(Stream stream) : HttpContent {
		protected override bool TryComputeLength(out long length) {
			length = 0;
			return false;
		}

		protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => stream.CopyToAsync(destination);
		protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
	}

	private sealed class BlockingStream : MemoryStream {
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
			Started.TrySetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
			return 0;
		}
	}
}
