namespace Pengin1011.Services.Discord;

public static class MessageSplitter {
	public const int DefaultLimit = 1900;

	public static IReadOnlyList<string> Split(string? text, int limit = DefaultLimit) {
		if (limit <= 0) limit = DefaultLimit;
		if (string.IsNullOrEmpty(text)) return [];
		if (text.Length <= limit) return [text];

		var chunks = new List<string>();
		var start = 0;
		while (start < text.Length) {
			var end = Math.Min(start + limit, text.Length);
			if (end - start < limit) {
				AppendChunk(chunks, text[start..]);
				break;
			}

			var window = text[start..end];
			var cut = window.LastIndexOf('\n');
			var consumed = 1;
			if (cut < 0) {
				cut = window.LastIndexOf(' ');
			}
			if (cut < 0) {
				cut = window.Length;
				consumed = 0;
				if (cut > 0 && char.IsHighSurrogate(window[cut - 1])) cut--;
			}

			var next = start + cut + consumed;
			if (next <= start) next = start + 1;
			AppendChunk(chunks, window[..cut]);
			start = next;
		}
		return chunks;
	}

	private static void AppendChunk(List<string> chunks, string chunk) {
		if (!string.IsNullOrWhiteSpace(chunk)) chunks.Add(chunk);
	}
}
