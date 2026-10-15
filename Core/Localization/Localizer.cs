using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Pengin1011.Core.Localization;

public static class Localizer {
	private static readonly ResourceManager _manager = new("Pengin1011.Resources.Strings", typeof(Localizer).Assembly);
	private static readonly ConcurrentDictionary<Assembly, Dictionary<string, Dictionary<string, string>>> _modules = new();

	public static void RegisterModule(Assembly assembly) {
		if (_modules.ContainsKey(assembly)) return;
		_modules.TryAdd(assembly, LoadModuleTables(assembly));
	}

	public static string Get(Assembly scope, string key, CultureInfo? culture = null) {
		if (_modules.TryGetValue(scope, out var tables) && TryLookup(tables, key, culture ?? CultureInfo.CurrentUICulture, out var text)) {
			return text;
		}
		return _manager.GetString(key, culture) ?? key;
	}

	public static string Format(Assembly scope, string key, params object?[] args) {
		return FormatCore(scope, CultureInfo.CurrentCulture, null, key, args);
	}

	public static string Format(Assembly scope, CultureInfo culture, string key, params object?[] args) {
		return FormatCore(scope, culture, culture, key, args);
	}

	private static string FormatCore(Assembly scope, CultureInfo formatCulture, CultureInfo? lookupCulture, string key, object?[] args) {
		return string.Format(formatCulture, Get(scope, key, lookupCulture), args);
	}

	private static bool TryLookup(Dictionary<string, Dictionary<string, string>> tables, string key, CultureInfo culture, out string text) {
		for (var current = culture; ; current = current.Parent) {
			if (tables.TryGetValue(current.Name, out var table) && table.TryGetValue(key, out text!)) return true;
			if (current.Name.Length == 0) break;
		}
		text = "";
		return false;
	}

	private static Dictionary<string, Dictionary<string, string>> LoadModuleTables(Assembly assembly) {
		var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
		foreach (var name in assembly.GetManifestResourceNames()) {
			if (!TryParseResourceName(name, out var culture)) continue;
			using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(Format(typeof(Localizer).Assembly, "ModuleLocalizationStreamMissing", name));
			var table = new Dictionary<string, string>(StringComparer.Ordinal);
			using var reader = new ResourceReader(stream);
			foreach (DictionaryEntry entry in reader) {
				if (entry.Key is string key && entry.Value is string value) table[key] = value;
			}
			tables[culture] = table;
		}
		return tables;
	}

	internal static bool TryParseResourceName(string name, out string culture) {
		culture = "";
		const string suffix = ".resources";
		const string marker = ".Localization";
		if (!name.EndsWith(suffix, StringComparison.Ordinal)) return false;
		var logical = name[..^suffix.Length];
		if (!logical.EndsWith(".Strings", StringComparison.Ordinal)) return false;
		var head = logical[..^".Strings".Length];
		var index = head.LastIndexOf(marker, StringComparison.Ordinal);
		if (index < 0) return false;
		var tail = head[(index + marker.Length)..];
		if (tail.Length == 0) return true;
		if (tail[0] != '.') return false;
		culture = ParseCultureName(tail[1..].Replace('_', '-'));
		return true;
	}

	private static string ParseCultureName(string segment) {
		if (TryGetCulture(segment, out var culture)) return culture.Name;
		throw new InvalidOperationException(Format(typeof(Localizer).Assembly, "ModuleLocalizationCultureInvalid", segment));
	}

	private static bool TryGetCulture(string value, out CultureInfo culture) {
		try {
			culture = CultureInfo.GetCultureInfo(value);
			return true;
		} catch (ArgumentException) {
			culture = null!;
			return false;
		}
	}
}
