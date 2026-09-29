using System.Globalization;
using System.Resources;

namespace Pengin1011.Core.Localization;

public static class Localizer {
	private static readonly ResourceManager Manager = new("Pengin1011.Resources.Strings", typeof(Localizer).Assembly);

	public static string Get(string key, CultureInfo? culture = null) {
		return Manager.GetString(key, culture) ?? key;
	}

	public static string Format(string key, params object?[] args) {
		return string.Format(CultureInfo.CurrentCulture, Get(key), args);
	}

	public static string Format(CultureInfo culture, string key, params object?[] args) {
		return string.Format(culture, Get(key, culture), args);
	}
}
