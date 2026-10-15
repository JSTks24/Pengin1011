using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Pengin1011Generator.AIPreset;

namespace Pengin1011.Tests;

public sealed class AIPresetGeneratorLocalizationTests {
	private static readonly CultureInfo _invariant = CultureInfo.InvariantCulture;
	private static readonly CultureInfo _chinese = new("zh-CN");

	private static ResourceManager Manager() {
		return new ResourceManager("Pengin1011Generator.AIPreset.Resources.DiagnosticStrings", typeof(AIPresetUsageGenerator).Assembly);
	}

	private static Dictionary<string, string> Table(CultureInfo culture) {
		var set = Manager().GetResourceSet(culture, true, false)
			?? throw new InvalidOperationException($"{culture.Name} resource set missing");
		var table = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (System.Collections.DictionaryEntry entry in set) {
			table[(string)entry.Key] = (string)entry.Value!;
		}
		return table;
	}

	private static IEnumerable<DiagnosticDescriptor> Descriptors() {
		var type = typeof(AIPresetDiagnostics);
		foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)) {
			if (field.FieldType == typeof(DiagnosticDescriptor) && field.GetValue(null) is DiagnosticDescriptor descriptor) yield return descriptor;
		}
	}

	private static HashSet<int> Placeholders(string text) {
		var result = new HashSet<int>();
		foreach (Match match in Regex.Matches(text, @"\{(\d+)\}")) {
			if (match.Groups[1].Value.Length > 0) result.Add(int.Parse(match.Groups[1].Value));
		}
		return result;
	}

	[Fact]
	public void NeutralAndChineseTables_CoverTheSameKeys() {
		var neutral = Table(_invariant);
		var chinese = Table(_chinese);

		Assert.Equal([.. neutral.Keys.OrderBy(k => k, StringComparer.Ordinal)], [.. chinese.Keys.OrderBy(k => k, StringComparer.Ordinal)]);
	}

	[Fact]
	public void Placeholders_MatchBetweenCultures() {
		var neutral = Table(_invariant);
		var chinese = Table(_chinese);
		foreach (var key in neutral.Keys) {
			Assert.Equal([.. Placeholders(neutral[key]).OrderBy(v => v)], [.. Placeholders(chinese[key]).OrderBy(v => v)]);
		}
	}

	[Fact]
	public void Descriptors_UseLocalizableResourceString() {
		foreach (var descriptor in Descriptors()) {
			Assert.IsType<LocalizableResourceString>(descriptor.MessageFormat);
			Assert.IsType<LocalizableResourceString>(descriptor.Title);
		}
	}

	[Fact]
	public void Messages_ChangeWithCulture() {
		foreach (var descriptor in Descriptors()) {
			Assert.NotEqual(descriptor.MessageFormat.ToString(_invariant), descriptor.MessageFormat.ToString(_chinese));
		}
	}

	[Fact]
	public void DiagnosticIds_RemainStableAndUnique() {
		var descriptors = Descriptors().ToList();
		var ids = descriptors.Select(d => d.Id).ToList();

		Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
		Assert.All(descriptors, d => Assert.Equal("AIPreset", d.Category));
		Assert.All(descriptors, d => Assert.Equal(DiagnosticSeverity.Error, d.DefaultSeverity));
		Assert.Contains("PAP001", ids);
	}

	[Fact]
	public void UnsupportedCulture_FallsBackToEnglish() {
		Assert.Equal(
			"The AI preset name at this call site must be a string literal or a const: {0}",
			Manager().GetString("PresetNameNotConstant", new CultureInfo("de-DE")));
	}
}
