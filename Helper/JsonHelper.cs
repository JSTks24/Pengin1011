using System.Text.Json;
using System.Text.Json.Serialization;
using QingQiu1011.Core.Logging;

namespace QingQiu1011.Helper;

public static class JsonHelper {
	public static readonly JsonSerializerOptions Options = new() {
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() },
	};

	public static T Load<T>(string path) where T : class {
		using var stream = File.OpenRead(path);
		var value = JsonSerializer.Deserialize<T>(stream, Options);
		if (value == null) {
			throw new JsonException($"JSON 根节点必须为对象，不能是 null：{path}");
		}
		return value;
	}

	public static bool EnsureTemplateFile(string path, string template) {
		if (File.Exists(path)) return false;
		var dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
		using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
			using var writer = new StreamWriter(stream);
			writer.Write(template);
		}
		return true;
	}

	public static bool Save<T>(string path, T value) {
		var tmp = $"{path}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
		try {
			var dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
			using (var stream = File.Create(tmp)) {
				JsonSerializer.Serialize(stream, value, Options);
			}
			File.Move(tmp, path, true);
			return true;
		} catch (Exception e) {
			Logger.Error(typeof(JsonHelper), e, $"写入 JSON 失败：{path}");
			try { File.Delete(tmp); } catch (Exception ex) { Logger.Error(typeof(JsonHelper), ex, $"清理 JSON 临时文件失败：{tmp}"); }
			return false;
		}
	}
}
