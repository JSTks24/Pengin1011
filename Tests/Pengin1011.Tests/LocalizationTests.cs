using System.Globalization;
using System.Resources;
using Pengin1011.Core;
using Pengin1011.Core.Localization;

namespace Pengin1011.Tests;

public sealed class LocalizationTests {
	[Fact]
	public void Get_InvariantCulture_ReturnsNeutralEnglish() {
		Assert.Equal("Startup failed", Loc.Get("StartupFailed", CultureInfo.InvariantCulture));
		Assert.Equal("Framework is exiting", Loc.Get("FrameworkExiting", CultureInfo.InvariantCulture));
	}

	[Fact]
	public void Get_ZhCN_ReturnsChinese() {
		Assert.Equal("启动失败", Loc.Get("StartupFailed", new CultureInfo("zh-CN")));
		Assert.Equal("框架正在退出", Loc.Get("FrameworkExiting", new CultureInfo("zh-CN")));
	}

	[Fact]
	public void Format_PositionalPlaceholder_BothCultures() {
		Assert.Equal(
			"Unknown command: demo (type help to see the command list)",
			Loc.Format(CultureInfo.InvariantCulture, "UnknownCommand", "demo"));
		Assert.Equal(
			"未知命令：demo（输入 help 查看命令列表）",
			Loc.Format(new CultureInfo("zh-CN"), "UnknownCommand", "demo"));
	}

	[Fact]
	public void Format_MultipleArgs_Invariant() {
		Assert.Equal(
			"Full database backup finished (2/5 databases succeeded)",
			Loc.Format(CultureInfo.InvariantCulture, "DbBackupCompleted", 2, 5));
	}

	[Fact]
	public void Format_MultipleArgs_ZhCN() {
		Assert.Equal(
			"全库备份完成（成功 2/5 库）",
			Loc.Format(new CultureInfo("zh-CN"), "DbBackupCompleted", 2, 5));
	}

	[Fact]
	public void Get_MissingKey_ReturnsKeyItself() {
		Assert.Equal("NoSuchKeyDefinitely", Loc.Get("NoSuchKeyDefinitely", CultureInfo.InvariantCulture));
		Assert.Equal("NoSuchKeyDefinitely", Loc.Get("NoSuchKeyDefinitely"));
	}

	[Fact]
	public void Format_MissingKey_ReturnsKeyItself() {
		Assert.Equal("NoSuchKeyDefinitely", Loc.Format(CultureInfo.InvariantCulture, "NoSuchKeyDefinitely", 1));
	}

	[Fact]
	public void Format_ExtraArgs_IgnoredWithoutThrow() {
		Assert.Equal(
			"Backup failed; see the error log for details",
			Loc.Format(CultureInfo.InvariantCulture, "BackupFailedSeeLog", 1, 2));
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
