using Pengin1011.Core;
using Pengin1011.Core.Logging;
using Pengin1011.Helper;

namespace Pengin1011.Modules.__MODULE__;

public static class __MODULE__Config {
	private static string FilePath => Path.Combine(AppContext.BaseDirectory, "config", "__MODULE__.json");

	private const string Template = "{}";

	public static __MODULE__Options Load() {
		if (JsonHelper.EnsureTemplateFile(FilePath, Template)) {
			Logger.Info(typeof(__MODULE__Config), Loc.Get("ConfigTemplateCreated"));
		}
		return JsonHelper.Load<__MODULE__Options>(FilePath);
	}
}
