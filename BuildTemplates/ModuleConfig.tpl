using QingQiu1011.Core.Logging;
using QingQiu1011.Helper;

namespace QingQiu1011.Modules.__MODULE__;

public static class __MODULE__Config {
	private static string FilePath => Path.Combine(AppContext.BaseDirectory, "config", "__MODULE__.json");

	private const string Template = "{}";

	public static __MODULE__Options Load() {
		if (JsonHelper.EnsureTemplateFile(FilePath, Template)) {
			Logger.Info(typeof(__MODULE__Config), "配置模板已生成：config/__MODULE__.json");
		}
		return JsonHelper.Load<__MODULE__Options>(FilePath);
	}
}

public class __MODULE__Options {
}
