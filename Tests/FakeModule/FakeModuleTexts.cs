using System.Globalization;
using Pengin1011.Core;

namespace Pengin1011.Modules.FakeModule;

public static class FakeModuleTexts {
	public static string GetText(string key) {
		return Loc.Get(key);
	}

	public static string FormatText(string key, params object?[] args) {
		return Loc.Format(key, args);
	}

	public static string FormatText(CultureInfo culture, string key, params object?[] args) {
		return Loc.Format(culture, key, args);
	}
}
