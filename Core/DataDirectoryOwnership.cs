namespace Pengin1011.Core;

public sealed class DataDirectoryOwnership : IDisposable {
	public const string LockFileName = ".pengin1011-owner.lock";
	private readonly FileStream _stream;

	private DataDirectoryOwnership(string directory, FileStream stream) {
		Directory = directory;
		_stream = stream;
	}

	public string Directory { get; }

	public static bool TryAcquire(string directory, out DataDirectoryOwnership? ownership, out string? failure) {
		ownership = null;
		failure = null;
		try {
			System.IO.Directory.CreateDirectory(directory);
			var stream = new FileStream(Path.Combine(directory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			ownership = new DataDirectoryOwnership(DataDirectoryResolver.Canonicalize(directory), stream);
			return true;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			failure = e.Message;
			return false;
		}
	}

	public void Dispose() {
		_stream.Dispose();
	}
}
