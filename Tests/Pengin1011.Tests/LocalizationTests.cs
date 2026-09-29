using System.Globalization;
using System.Resources;
using Pengin1011.Core;
using Pengin1011.Core.Localization;

namespace Pengin1011.Tests;

public sealed class LocalizationTests {
	[Fact]
	public void Get_InvariantCulture_ReturnsNeutralEnglish() {
		Assert.Equal("Startup failed", Localizer.Get("StartupFailed", CultureInfo.InvariantCulture));
		Assert.Equal("Framework is exiting", Localizer.Get("FrameworkExiting", CultureInfo.InvariantCulture));
	}

	[Fact]
	public void Get_ZhCN_ReturnsChinese() {
		Assert.Equal("启动失败", Localizer.Get("StartupFailed", new CultureInfo("zh-CN")));
		Assert.Equal("框架正在退出", Localizer.Get("FrameworkExiting", new CultureInfo("zh-CN")));
	}

	[Fact]
	public void Format_PositionalPlaceholder_BothCultures() {
		Assert.Equal(
			"Gateway did not become ready within 45s; startup failed.",
			Localizer.Format(CultureInfo.InvariantCulture, "GatewayStartupTimeout", 45d));
		Assert.Equal(
			"网关未在 45s 内就绪，按启动失败处理。",
			Localizer.Format(new CultureInfo("zh-CN"), "GatewayStartupTimeout", 45d));
	}

	[Fact]
	public void Format_MultipleArgs_Invariant() {
		Assert.Equal(
			"Configuration loaded Provider=Gemini Model=m1 MaxParallel=5",
			Localizer.Format(CultureInfo.InvariantCulture, "ConfigLoaded", AIProvider.Gemini, "m1", 5));
	}

	[Fact]
	public void Get_MissingKey_ReturnsKeyItself() {
		Assert.Equal("NoSuchKeyDefinitely", Localizer.Get("NoSuchKeyDefinitely", CultureInfo.InvariantCulture));
		Assert.Equal("NoSuchKeyDefinitely", Localizer.Get("NoSuchKeyDefinitely"));
	}

	[Fact]
	public void Format_MissingKey_ReturnsKeyItself() {
		Assert.Equal("NoSuchKeyDefinitely", Localizer.Format(CultureInfo.InvariantCulture, "NoSuchKeyDefinitely", 1));
	}

	[Fact]
	public void Format_ExtraArgs_IgnoredWithoutThrow() {
		Assert.Equal(
			"Backup failed; see the error log for details",
			Localizer.Format(CultureInfo.InvariantCulture, "BackupFailedSeeLog", 1, 2));
	}

	[Fact]
	public void Resources_DoNotContainTestProbeKeys() {
		var manager = new ResourceManager("Pengin1011.Resources.Strings", typeof(Localizer).Assembly);
		var probeKeys = new[] {
			"ProbeTimeout",
			"ProbeConfigLoadFailed",
			"ProbeModuleInitIncomplete",
			"ProbeFakeModuleMissing",
			"ProbeDbContextTypeMissing",
			"ProbeRowMismatch",
			"ProbeEntityTypeMissing",
			"ProbeExitReportFailed",
		};
		foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("zh-CN") }) {
			var set = manager.GetResourceSet(culture, true, false)
				?? throw new InvalidOperationException($"{culture} resource set missing");
			var keys = new HashSet<string>();
			foreach (System.Collections.DictionaryEntry entry in set) {
				keys.Add((string)entry.Key);
			}
			foreach (var key in probeKeys) {
				Assert.False(keys.Contains(key), $"{key} must not exist in the {culture} resource set");
			}
		}
	}

	[Fact]
	public void Resources_ZhCN_CoversEveryNeutralKey() {
		var manager = new ResourceManager("Pengin1011.Resources.Strings", typeof(Localizer).Assembly);
		var neutral = manager.GetResourceSet(CultureInfo.InvariantCulture, true, true)
			?? throw new InvalidOperationException("neutral resource set missing");
		var zh = manager.GetResourceSet(new CultureInfo("zh-CN"), true, false)
			?? throw new InvalidOperationException("zh-CN resource set missing");
		var zhKeys = new HashSet<string>();
		foreach (System.Collections.DictionaryEntry entry in zh) {
			zhKeys.Add((string)entry.Key);
		}
		var missing = new List<string>();
		foreach (System.Collections.DictionaryEntry entry in neutral) {
			var key = (string)entry.Key;
			if (!zhKeys.Contains(key)) missing.Add(key);
		}
		Assert.True(missing.Count == 0, $"missing zh-CN translations: {string.Join(",", missing)}");
	}
}
