namespace Pengin1011.Tests;

internal sealed class TempConfigFile : IDisposable {
	public string Path { get; }

	public TempConfigFile(string content) {
		Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pengin1011_{Guid.NewGuid():N}.json");
		File.WriteAllText(Path, content);
	}

	public void Dispose() {
		File.Delete(Path);
	}
}
