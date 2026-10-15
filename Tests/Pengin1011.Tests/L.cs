using Pengin1011.Core;

namespace Pengin1011.Tests;

internal static class L {
	public static string Get(string key) {
		return Loc.Get(key);
	}

	public static string Prefix(string key) {
		var value = Loc.Get(key);
		var index = value.IndexOf('{');
		return index < 0 ? value : value[..index].TrimEnd();
	}
}
