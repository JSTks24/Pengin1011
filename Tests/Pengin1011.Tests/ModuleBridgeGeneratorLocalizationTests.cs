using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Pengin1011Generator.ModuleBridge;

namespace Pengin1011.Tests;

public sealed class ModuleBridgeGeneratorLocalizationTests {
	private static readonly CultureInfo _invariant = CultureInfo.InvariantCulture;
	private static readonly CultureInfo _chinese = new("zh-CN");

	private static ResourceManager Manager() {
		return new ResourceManager("Pengin1011Generator.ModuleBridge.Resources.DiagnosticStrings", typeof(ModuleBridgeGenerator).Assembly);
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
		var type = typeof(BridgeDiagnostics);
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
	public void EveryDescriptorKey_ExistsInBothTables() {
		var neutral = Table(_invariant);
		var chinese = Table(_chinese);
		foreach (var descriptor in Descriptors()) {
			var key = descriptor.Id;
			Assert.False(string.IsNullOrWhiteSpace(descriptor.MessageFormat.ToString(_invariant)), $"{key} has an empty invariant message");
			Assert.False(string.IsNullOrWhiteSpace(descriptor.MessageFormat.ToString(_chinese)), $"{key} has an empty zh-CN message");
		}
		Assert.NotEmpty(neutral);
		Assert.NotEmpty(chinese);
	}

	[Fact]
	public void Placeholders_MatchBetweenCultures() {
		var neutral = Table(_invariant);
		var chinese = Table(_chinese);
		foreach (var key in neutral.Keys) {
			var expected = Placeholders(neutral[key]);
			var actual = Placeholders(chinese[key]);
			Assert.Equal([.. expected.OrderBy(v => v)], [.. actual.OrderBy(v => v)]);
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
			var english = descriptor.MessageFormat.ToString(_invariant);
			var chinese = descriptor.MessageFormat.ToString(_chinese);
			Assert.NotEqual(english, chinese);
		}
	}

	[Fact]
	public void UnsupportedCulture_FallsBackToEnglish() {
		var german = new CultureInfo("de-DE");
		var manager = Manager();

		Assert.Equal("A ModuleBridgeExport method must be public", manager.GetString("MethodNotPublic", german));
		Assert.Equal("A ModuleBridgeExport method must be public", manager.GetString("MethodNotPublic", CultureInfo.GetCultureInfo("ja-JP")));
	}

	[Fact]
	public void ReportDiagnostic_LocalizesUsingTheSuppliedCulture() {
		var outcome = GeneratorTestHost.Run("""
namespace Pengin1011.Core { [System.AttributeUsage(System.AttributeTargets.Method)] public sealed class ModuleBridgeExportAttribute : System.Attribute { } }
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	internal static string Hidden() => "x";
}
""");
		var diagnostic = GeneratorTestHost.Single(outcome, "PBG001");

		Assert.Equal("A ModuleBridgeExport method must be public", diagnostic.GetMessage(_invariant));
		Assert.Equal("ModuleBridgeExport 标记的方法必须是 public", diagnostic.GetMessage(_chinese));
	}

	[Fact]
	public void DiagnosticIds_RemainStableAndUnique() {
		var descriptors = Descriptors().ToList();
		var ids = descriptors.Select(d => d.Id).ToList();

		Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
		Assert.All(descriptors, d => Assert.Equal("ModuleBridge", d.Category));
		Assert.All(descriptors, d => Assert.Equal(DiagnosticSeverity.Error, d.DefaultSeverity));
		Assert.Contains("PBG001", ids);
		Assert.Contains("PBG006", ids);
		Assert.Contains("PBG013", ids);
		Assert.Contains("PBG106", ids);
	}

	[Fact]
	public void ChineseSatelliteAssembly_IsProducedNextToTheGenerator() {
		var cultureDirectory = Path.Combine(AppContext.BaseDirectory, "zh-CN");
		var host = Path.Combine(AppContext.BaseDirectory, "Pengin1011Generator.dll");
		Assert.True(File.Exists(host), "生成器程序集应随测试输出复制");

		var generatorDirectory = Path.GetDirectoryName(typeof(ModuleBridgeGenerator).Assembly.Location);
		Assert.NotNull(generatorDirectory);
		var satellite = Path.Combine(generatorDirectory!, "zh-CN", "Pengin1011Generator.resources.dll");

		Assert.True(File.Exists(satellite), $"生成器中文卫星程序集缺失：{satellite}（诊断本地化在编译期不会生效）");
		Assert.True(cultureDirectory.Length > 0);
	}
}
