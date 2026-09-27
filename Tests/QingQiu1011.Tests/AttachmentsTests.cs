using QingQiu1011.Services.Discord;

namespace QingQiu1011.Tests;

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
}
