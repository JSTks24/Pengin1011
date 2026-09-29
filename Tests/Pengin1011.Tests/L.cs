using Pengin1011.Core.Localization;

namespace Pengin1011.Tests;

internal static class L {
	public static string Get(string key) {
		return Localizer.Get(key);
	}

	public static string Prefix(string key) {
		var value = Localizer.Get(key);
		var index = value.IndexOf('{');
		return index < 0 ? value : value[..index].TrimEnd();
	}
}
