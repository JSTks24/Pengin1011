using Pengin1011.Core;
using Pengin1011.Core.Logging;

namespace Pengin1011.Tests;

public sealed class AppConfigTests {
	private const string ValidConfig = """
		{
		  "Discord": { "Token": "tok-1" },
		  "Logger": { "Channels": ["111222333"] }
		}
		""";

	[Fact]
	public void TryLoad_ValidConfig() {
		using var cfg = new TempConfigFile(ValidConfig);
		Assert.True(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Empty(errors);
		Assert.Equal("tok-1", AppConfig.Discord.Token);
		Assert.Equal([111222333UL], AppConfig.Logging.Channels);
	}

	[Fact]
	public void TryLoad_LoggerChannelsParsed() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "Logger": { "Channels": ["111222333", "444555666"] }
			}
			""");
		Assert.True(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Empty(errors);
		Assert.Equal([111222333UL, 444555666UL], AppConfig.Logging.Channels);
	}

	[Fact]
	public void TryLoad_LoggerChannelInvalid() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "Logger": { "Channels": ["not-a-number"] }
			}
			""");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains(L.Prefix("LoggerChannelInvalid")));
		Assert.Contains(errors, static e => e.Contains("not-a-number"));
	}

	[Fact]
	public void TryLoad_LoggerSectionOptional() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" }
			}
			""");
		Assert.True(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Empty(errors);
		Assert.Empty(AppConfig.Logging.Channels);
	}

	[Fact]
	public void TryLoad_LoggerChannelZeroRejected() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "Logger": { "Channels": ["0"] }
			}
			""");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains(L.Prefix("LoggerChannelInvalid")));
	}

	[Fact]
	public void TryLoad_LegacyAiSectionIgnored() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "AI": {
			    "Provider": "openai",
			    "MaxParallel": 3,
			    "OpenAI": { "ApiKey": "k", "BaseUrl": "https://api.example.com/v1", "Model": "m1" }
			  }
			}
			""");
		Assert.True(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Empty(errors);
		Assert.Equal("tok", AppConfig.Discord.Token);
		Assert.Empty(AppConfig.Logging.Channels);
	}

	[Fact]
	public void TryLoad_MissingFile_CreatesFromTemplate() {
		var path = Path.Combine(Path.GetTempPath(), $"dcfox_missing_{Guid.NewGuid():N}.json");
		try {
			Assert.False(AppConfig.TryLoad(path, out var errors));
			Assert.Contains(errors, static e => e.Contains(L.Prefix("ConfigCreatedFromTemplate")));
			Assert.True(File.Exists(path));
			Assert.True(AppConfig.PathConfigurationValid);
			var content = File.ReadAllText(path);
			Assert.Contains("\"Discord\"", content);
			Assert.Contains("\"Token\"", content);
			Assert.DoesNotContain("\"AI\"", content);
		} finally {
			try { File.Delete(path); } catch (IOException) { }
		}
	}

	[Fact]
	public void TryLoad_ExistingFile_NotOverwrittenByTemplate() {
		using var cfg = new TempConfigFile(ValidConfig);
		Assert.True(AppConfig.TryLoad(cfg.Path, out _));
		Assert.Equal(ValidConfig, File.ReadAllText(cfg.Path));
	}

	[Fact]
	public void DefaultPath_LivesInConfigDirectory() {
		Assert.EndsWith(Path.Combine("config", "config.json"), AppConfig.DefaultPath);
	}

	[Fact]
	public void TryLoad_BadJson() {
		using var cfg = new TempConfigFile("{ not json");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.False(AppConfig.PathConfigurationValid);
		Assert.Contains(errors, static e => e.Contains(L.Prefix("ConfigReadFailed")));
	}

	[Fact]
	public void TryLoad_MissingToken() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "" }
			}
			""");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains("Discord.Token"));
	}

	[Fact]
	public void TryLoad_ReportsAllErrorsAtOnce() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "" },
			  "Logger": { "Channels": ["bad", "0"] }
			}
			""");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.True(errors.Count >= 3, $"expected >= 3 errors, got {errors.Count}");
	}

	[Fact]
	public void TryLoad_FailureKeepsPreviousConfig() {
		using var first = new TempConfigFile(ValidConfig);
		Assert.True(AppConfig.TryLoad(first.Path, out _));
		Assert.Equal("tok-1", AppConfig.Discord.Token);

		using var broken = new TempConfigFile("""
			{
			  "Discord": { "Token": "" }
			}
			""");
		Assert.False(AppConfig.TryLoad(broken.Path, out _));
		Assert.Equal("tok-1", AppConfig.Discord.Token);
		Assert.Equal([111222333UL], AppConfig.Logging.Channels);
	}

	[Fact]
	public void TryLoad_NullRoot_FailsWithExplicitError() {
		using var cfg = new TempConfigFile("null");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains(L.Prefix("ConfigRootMustBeObject")));
	}

	[Fact]
	public void TryLoad_EmptyFile_Fails() {
		using var cfg = new TempConfigFile("");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.NotEmpty(errors);
	}

	[Fact]
	public void TryLoad_ArrayRoot_Fails() {
		using var cfg = new TempConfigFile("[]");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.NotEmpty(errors);
	}

	[Fact]
	public void TryLoad_EmptyObject_FailsWithMissingFieldErrors() {
		using var cfg = new TempConfigFile("{}");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains("Discord.Token"));
		Assert.Single(errors);
	}

	[Fact]
	public void TryLoad_NullRootKeepsPreviousConfig() {
		using var first = new TempConfigFile(ValidConfig);
		Assert.True(AppConfig.TryLoad(first.Path, out _));

		using var broken = new TempConfigFile("null");
		Assert.False(AppConfig.TryLoad(broken.Path, out var errors));
		Assert.NotEmpty(errors);
		Assert.Equal("tok-1", AppConfig.Discord.Token);
		Assert.Equal([111222333UL], AppConfig.Logging.Channels);
	}

	[Fact]
	public void TryLoad_InvalidLoggerChannel_WritesErrorLog() {
		var logPath = TempErrorLog();
		Logger.SetLogPathForTest(logPath);
		try {
			using var cfg = new TempConfigFile("""
				{
				  "Discord": { "Token": "tok" },
				  "Logger": { "Channels": ["not-a-number"] }
				}
				""");
			Assert.False(AppConfig.TryLoad(cfg.Path, out _));
			var log = File.ReadAllText(logPath);
			Assert.Contains(L.Get("LoggerChannelInvalid").Split('{')[0].TrimEnd(), log);
		} finally {
			CleanupErrorLog(logPath);
		}
	}

	private static string TempErrorLog() {
		return Path.Combine(Path.GetTempPath(), $"qq1011_cfg_{Guid.NewGuid():N}", "error.log");
	}

	private static void CleanupErrorLog(string path) {
		Logger.SetLogPathForTest(null);
		var directory = Path.GetDirectoryName(path)!;
		if (Directory.Exists(directory)) Directory.Delete(directory, true);
	}
}

file sealed class TempConfigFile : IDisposable {
	public string Path { get; }

	public TempConfigFile(string content) {
		Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dcfox_{Guid.NewGuid():N}.json");
		File.WriteAllText(Path, content);
	}

	public void Dispose() {
		try { File.Delete(Path); } catch (IOException) { }
	}
}
