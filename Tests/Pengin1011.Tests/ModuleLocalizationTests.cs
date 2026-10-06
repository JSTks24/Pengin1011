using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using Pengin1011.Core;
using Pengin1011.Core.Localization;
using Pengin1011.Modules.FakeBare;
using Pengin1011.Modules.FakeBrokenLocale;
using Pengin1011.Modules.FakeModule;
using Pengin1011.Modules.FakeSecond;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ModuleLocalizationTests {
	private static readonly CultureInfo Zh = new("zh-CN");

	private static Assembly FakeModuleAssembly => typeof(FakeModuleTexts).Assembly;

	private static Assembly FakeSecondAssembly => typeof(FakeSecondTexts).Assembly;

	[Fact]
	public void ExplicitScope_ReadsModuleTables_PerCulture() {
		Localizer.RegisterModule(FakeModuleAssembly);

		Assert.Equal("hello from FakeModule", Localizer.Get(FakeModuleAssembly, "FakeHello", CultureInfo.InvariantCulture));
		Assert.Equal("来自 FakeModule 的问候", Localizer.Get(FakeModuleAssembly, "FakeHello", Zh));
	}

	[Fact]
	public void ModuleCall_AutoRoutesToOwnAssembly() {
		Localizer.RegisterModule(FakeModuleAssembly);

		var original = CultureInfo.CurrentUICulture;
		CultureInfo.CurrentUICulture = Zh;
		try {
			Assert.Equal("来自 FakeModule 的问候", FakeModuleTexts.GetText("FakeHello"));
		} finally {
			CultureInfo.CurrentUICulture = original;
		}
	}

	[Fact]
	public void ModuleCall_Format_UsesModuleTable() {
		Localizer.RegisterModule(FakeModuleAssembly);

		var original = CultureInfo.CurrentUICulture;
		CultureInfo.CurrentUICulture = Zh;
		try {
			Assert.Equal("来自 FakeModule 的问候 7", FakeModuleTexts.FormatText("FakeHelloWithArg", 7));
		} finally {
			CultureInfo.CurrentUICulture = original;
		}
	}

	[Fact]
	public void ModuleCall_FormatWithCulture_AutoRoutesToModuleTable() {
		Localizer.RegisterModule(FakeModuleAssembly);

		Assert.Equal("来自 FakeModule 的问候 7", FakeModuleTexts.FormatText(Zh, "FakeHelloWithArg", 7));
		Assert.Equal("hello from FakeModule 7", FakeModuleTexts.FormatText(CultureInfo.InvariantCulture, "FakeHelloWithArg", 7));
	}

	[Fact]
	public void ModuleMiss_FallsBackToMainResources() {
		Localizer.RegisterModule(FakeModuleAssembly);

		Assert.Equal(Localizer.Get("CliHelp", Zh), Localizer.Get(FakeModuleAssembly, "CliHelp", Zh));
		Assert.Equal("NoSuchKeyAnywhere", Localizer.Get(FakeModuleAssembly, "NoSuchKeyAnywhere", Zh));
	}

	[Fact]
	public void ModuleTable_WinsOver_FrameworkTable_OnSameKey() {
		Localizer.RegisterModule(FakeModuleAssembly);
		var frameworkZh = Localizer.Get("ModuleUnavailable", Zh);
		var frameworkNeutral = Localizer.Get("ModuleUnavailable", CultureInfo.InvariantCulture);

		Assert.Equal("FakeModule 覆盖了这条主程序 key", Localizer.Get(FakeModuleAssembly, "ModuleUnavailable", Zh));
		Assert.Equal("FakeModule overrides this framework key", Localizer.Get(FakeModuleAssembly, "ModuleUnavailable", CultureInfo.InvariantCulture));
		Assert.NotEqual(frameworkZh, Localizer.Get(FakeModuleAssembly, "ModuleUnavailable", Zh));
		Assert.NotEqual(frameworkNeutral, Localizer.Get(FakeModuleAssembly, "ModuleUnavailable", CultureInfo.InvariantCulture));
		Assert.Equal(frameworkZh, Localizer.Get("ModuleUnavailable", Zh));
		Assert.Equal(frameworkNeutral, Localizer.Get("ModuleUnavailable", CultureInfo.InvariantCulture));
	}

	[Fact]
	public void CultureChain_MissesToModuleNeutral_BeforeMainResources() {
		Localizer.RegisterModule(FakeModuleAssembly);

		Assert.Equal("only FakeModule has this", Localizer.Get(FakeModuleAssembly, "FakeSharedOnlyHere", new CultureInfo("fr-FR")));
	}

	[Fact]
	public void Modules_AreIsolated_FromEachOther() {
		Localizer.RegisterModule(FakeModuleAssembly);
		Localizer.RegisterModule(FakeSecondAssembly);

		Assert.Equal("hello from FakeModule", Localizer.Get(FakeModuleAssembly, "FakeHello", CultureInfo.InvariantCulture));
		Assert.Equal("hello from FakeSecond", Localizer.Get(FakeSecondAssembly, "FakeHello", CultureInfo.InvariantCulture));
		Assert.Equal("only FakeModule has this", Localizer.Get(FakeModuleAssembly, "FakeSharedOnlyHere", CultureInfo.InvariantCulture));
		Assert.Equal("FakeSecondOnly", Localizer.Get(FakeModuleAssembly, "FakeSecondOnly", CultureInfo.InvariantCulture));
		Assert.Equal("only FakeSecond has this", Localizer.Get(FakeSecondAssembly, "FakeSecondOnly", CultureInfo.InvariantCulture));
		Assert.Equal("FakeSharedOnlyHere", Localizer.Get(FakeSecondAssembly, "FakeSharedOnlyHere", CultureInfo.InvariantCulture));
	}

	[Fact]
	public void ModuleAutoRoute_IsolatedFromOtherModules() {
		Localizer.RegisterModule(FakeModuleAssembly);
		Localizer.RegisterModule(FakeSecondAssembly);

		var original = CultureInfo.CurrentUICulture;
		CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
		try {
			Assert.Equal("only FakeModule has this", FakeModuleTexts.GetText("FakeSharedOnlyHere"));
			Assert.Equal("FakeSharedOnlyHere", FakeSecondTexts.GetText("FakeSharedOnlyHere"));
			Assert.Equal("FakeSecondOnly", FakeModuleTexts.GetText("FakeSecondOnly"));
			Assert.Equal("only FakeSecond has this", FakeSecondTexts.GetText("FakeSecondOnly"));
		} finally {
			CultureInfo.CurrentUICulture = original;
		}
	}

	[Fact]
	public void ModuleWithoutLocalization_RegistersEmpty_AndFallsBack() {
		var bare = typeof(FakeBareRuntime).Assembly;
		Localizer.RegisterModule(bare);

		Assert.Equal(Localizer.Get("CliHelp", Zh), Localizer.Get(bare, "CliHelp", Zh));
		Assert.Equal("FakeHello", Localizer.Get(bare, "FakeHello", Zh));
	}

	[Fact]
	public void Register_Twice_KeepsFirstCatalog() {
		Localizer.RegisterModule(FakeModuleAssembly);
		Localizer.RegisterModule(FakeModuleAssembly);

		Assert.Equal("hello from FakeModule", Localizer.Get(FakeModuleAssembly, "FakeHello", CultureInfo.InvariantCulture));
	}

	[Fact]
	public void ModuleResources_ZhCN_CoversEveryNeutralKey() {
		foreach (var assembly in new[] { FakeModuleAssembly, FakeSecondAssembly }) {
			var tables = ReadTables(assembly);
			var neutral = tables[""];
			var zh = tables["zh-CN"];
			var missing = neutral.Keys.Where(key => !zh.ContainsKey(key)).ToList();
			Assert.True(missing.Count == 0, $"{assembly.GetName().Name} missing zh-CN translations: {string.Join(",", missing)}");
		}
	}

	[Fact]
	public void BrokenModuleResources_ThrowOnRegister() {
		Assert.ThrowsAny<Exception>(() => Localizer.RegisterModule(typeof(FakeBrokenLocaleRuntime).Assembly));
		Assert.Equal(Localizer.Get("CliHelp", Zh), Localizer.Get(typeof(FakeBrokenLocaleRuntime).Assembly, "CliHelp", Zh));
	}

	[Fact]
	public void HostLoad_BrokenLocaleModule_LoadsAndFallsBack() {
		var previousDirectory = ModuleHost.ModuleDirectoryOverrideForTest;
		ModuleHost.ResetForTest();
		ModuleHost.ModuleDirectoryOverrideForTest = Path.Combine(AppContext.BaseDirectory, "brokenmodule");
		try {
			ModuleHost.LoadAll();

			var run = ModuleHost.Find("FakeBrokenLocale");
			Assert.NotNull(run);
			Assert.Equal(Localizer.Get("CliHelp", Zh), Localizer.Get(run!.Assembly, "CliHelp", Zh));
			Assert.Equal("FakeHello", Localizer.Get(run.Assembly, "FakeHello", Zh));
		} finally {
			ModuleHost.ModuleDirectoryOverrideForTest = previousDirectory;
			ModuleHost.ResetForTest();
		}
	}

	[Fact]
	public void HostLoad_HealthyModule_AutoRoutesToOwnTables() {
		var previousDirectory = ModuleHost.ModuleDirectoryOverrideForTest;
		ModuleHost.ResetForTest();
		ModuleHost.ModuleDirectoryOverrideForTest = null;
		try {
			ModuleHost.LoadAll();

			var run = ModuleHost.Find("FakeModule");
			Assert.NotNull(run);
			var original = CultureInfo.CurrentUICulture;
			CultureInfo.CurrentUICulture = Zh;
			try {
				Assert.Equal("来自 FakeModule 的问候", FakeModuleTexts.GetText("FakeHello"));
			} finally {
				CultureInfo.CurrentUICulture = original;
			}
			Assert.Equal("only FakeModule has this", Localizer.Get(run!.Assembly, "FakeSharedOnlyHere", CultureInfo.InvariantCulture));
		} finally {
			ModuleHost.ModuleDirectoryOverrideForTest = previousDirectory;
			ModuleHost.ResetForTest();
		}
	}

	[Fact]
	public void ModuleAssembly_EmbedsCultureTables_WithoutSatellite() {
		var names = FakeModuleAssembly.GetManifestResourceNames();

		Assert.Contains("Pengin1011.Modules.FakeModule.Localization.Strings.resources", names);
		Assert.Contains("Pengin1011.Modules.FakeModule.Localization.zh_CN.Strings.resources", names);
		Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "zh-CN", "FakeModule.resources.dll")));
	}

	[Theory]
	[InlineData("Pengin1011.Modules.Sample.Localization.Strings.resources", "")]
	[InlineData("Pengin1011.Modules.Sample.Localization.zh_CN.Strings.resources", "zh-CN")]
	[InlineData("Pengin1011.Modules.Sample.Localization.zh_Hans_CN.Strings.resources", "zh-Hans-CN")]
	[InlineData("Pengin1011.Modules.Sample.Localization.fr.Strings.resources", "fr")]
	[InlineData("Localization.Strings.resources", null)]
	[InlineData("Pengin1011.Modules.Sample.Resources.Strings.resources", null)]
	[InlineData("Pengin1011.Modules.Sample.LocalizationX.Strings.resources", null)]
	[InlineData("Pengin1011.Modules.Sample.Localization.Errors.resources", null)]
	[InlineData("Pengin1011.Modules.Sample.Localization.zh_CN.Errors.resources", null)]
	[InlineData("Pengin1011.Modules.Sample.Localization.zh_CN.Strings.resx", null)]
	public void TryParseResourceName_ClassifiesManifestNames(string name, string? expected) {
		var parsed = Localizer.TryParseResourceName(name, out var culture);

		Assert.Equal(expected != null, parsed);
		if (expected != null) Assert.Equal(expected, culture);
	}

	[Fact]
	public void TryParseResourceName_InvalidCultureSegment_Throws() {
		Assert.Throws<InvalidOperationException>(() => Localizer.TryParseResourceName("Pengin1011.Modules.Sample.Localization.not!a!culture.Strings.resources", out _));
	}

	private static Dictionary<string, Dictionary<string, string>> ReadTables(Assembly assembly) {
		var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal) { [""] = [], ["zh-CN"] = [] };
		foreach (var name in assembly.GetManifestResourceNames()) {
			var neutral = name.EndsWith(".Localization.Strings.resources", StringComparison.Ordinal);
			var zh = name.Contains(".Localization.zh_CN.Strings.resources", StringComparison.Ordinal)
				|| name.Contains(".Localization.zh-CN.Strings.resources", StringComparison.Ordinal);
			if (!neutral && !zh) continue;
			using var reader = new ResourceReader(assembly.GetManifestResourceStream(name)!);
			var table = tables[neutral ? "" : "zh-CN"];
			foreach (DictionaryEntry entry in reader) {
				if (entry.Key is string key && entry.Value is string value) table[key] = value;
			}
		}
		return tables;
	}
}
