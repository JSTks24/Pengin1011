using System.Text;

namespace Pengin1011.Core;

internal static class DatabaseMaintenance {
	internal static string MarkerPath(string directory, string id) {
		if (!DatabaseIdentity.IsSafeIdentifier(id)) throw new ArgumentException(Loc.Get("DatabaseNameInvalid"), nameof(id));
		return Path.Combine(directory, id + ".pending");
	}

	internal static bool Exists(string directory, string id) {
		try {
			File.GetAttributes(MarkerPath(directory, id));
			return true;
		} catch (FileNotFoundException) {
			return false;
		} catch (DirectoryNotFoundException) {
			return false;
		}
	}

	internal static void Write(string directory, string id, string operation) {
		var path = MarkerPath(directory, id);
		using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) {
			stream.Write(Encoding.UTF8.GetBytes(operation));
			stream.Flush(true);
		}
		DatabaseBackupStore.FlushDirectory(directory);
	}

	internal static void Clear(string directory, string id) {
		File.Delete(MarkerPath(directory, id));
		DatabaseBackupStore.FlushDirectory(directory);
	}
}
