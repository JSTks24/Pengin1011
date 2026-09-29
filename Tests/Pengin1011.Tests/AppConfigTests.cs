using Pengin1011.Core;

namespace Pengin1011.Tests;

public sealed class AppConfigTests {
	private const string ValidOpenAI = """
		{
		  "Discord": { "Token": "tok-1" },
		  "AI": {
		    "Provider": "openai",
		    "MaxParallel": 3,
		    "OpenAI": { "ApiKey": "k", "BaseUrl": "https://api.example.com/v1", "Model": "m1" },
		    "Gemini": { "ApiKey": "", "Project": "", "Location": "", "Model": "" }
		  }
		}
		""";

	[Fact]
	public void TryLoad_ValidOpenAI() {
		using var cfg = new TempConfigFile(ValidOpenAI);
		Assert.True(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Empty(errors);
		Assert.Equal("tok-1", AppConfig.Discord.Token);
		Assert.Equal(AIProvider.OpenAI, AppConfig.AI.Provider);
		Assert.Equal(3, AppConfig.AI.MaxParallel);
		Assert.Equal("m1", AppConfig.AI.OpenAI.Model);
		Assert.Equal("https://api.example.com/v1", AppConfig.AI.OpenAI.BaseUrl!.ToString());
		Assert.True(AppConfig.AI.Gemini.IsEmpty);
	}

	[Fact]
	public void TryLoad_MaxParallelDefaultsToFive() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "AI": {
			    "Provider": "openai",
			    "OpenAI": { "ApiKey": "k", "BaseUrl": "https://api.example.com/v1", "Model": "m1" }
			  }
			}
			""");
		Assert.True(AppConfig.TryLoad(cfg.Path, out _));
		Assert.Equal(5, AppConfig.AI.MaxParallel);
	}

	[Fact]
	public void TryLoad_GeminiWithApiKey() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "AI": {
			    "Provider": "gemini",
			    "Gemini": { "ApiKey": "gk", "Model": "gm" }
			  }
			}
			""");
		Assert.True(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Empty(errors);
		Assert.Equal(AIProvider.Gemini, AppConfig.AI.Provider);
		Assert.Equal("gk", AppConfig.AI.Gemini.ApiKey);
		Assert.Equal("gm", AppConfig.AI.Gemini.Model);
		Assert.True(AppConfig.AI.OpenAI.IsEmpty);
	}

	[Fact]
	public void TryLoad_GeminiWithVertex() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "AI": {
			    "Provider": "gemini",
			    "Gemini": { "Project": "p", "Location": "us-central1", "Model": "gm" }
			  }
			}
			""");
		Assert.True(AppConfig.TryLoad(cfg.Path, out _));
		Assert.Equal("p", AppConfig.AI.Gemini.Project);
		Assert.Equal("us-central1", AppConfig.AI.Gemini.Location);
	}

	[Fact]
	public void TryLoad_OpenAIWithFullGeminiBackup() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "AI": {
			    "Provider": "openai",
			    "OpenAI": { "ApiKey": "k", "BaseUrl": "https://api.example.com/v1", "Model": "m1" },
			    "Gemini": { "ApiKey": "gk", "Model": "gm" }
			  }
			}
			""");
		Assert.True(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Empty(errors);
	}

	[Fact]
	public void TryLoad_MissingFile_CreatesFromTemplate() {
		var path = Path.Combine(Path.GetTempPath(), $"dcfox_missing_{Guid.NewGuid():N}.json");
		try {
			Assert.False(AppConfig.TryLoad(path, out var errors));
			Assert.Contains(errors, static e => e.Contains("配置文件不存在"));
			Assert.True(File.Exists(path));
			var content = File.ReadAllText(path);
			Assert.Contains("\"Discord\"", content);
			Assert.Contains("\"Token\"", content);
		} finally {
			try { File.Delete(path); } catch (IOException) { }
		}
	}

	[Fact]
	public void TryLoad_ExistingFile_NotOverwrittenByTemplate() {
		using var cfg = new TempConfigFile(ValidOpenAI);
		Assert.True(AppConfig.TryLoad(cfg.Path, out _));
		Assert.Equal(ValidOpenAI, File.ReadAllText(cfg.Path));
	}

	[Fact]
	public void DefaultPath_LivesInConfigDirectory() {
		Assert.EndsWith(Path.Combine("config", "config.json"), AppConfig.DefaultPath);
	}

	[Fact]
	public void TryLoad_BadJson() {
		using var cfg = new TempConfigFile("{ not json");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains("配置文件读取或解析失败"));
	}

	[Fact]
	public void TryLoad_MissingToken() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "" },
			  "AI": {
			    "Provider": "openai",
			    "OpenAI": { "ApiKey": "k", "BaseUrl": "https://api.example.com/v1", "Model": "m1" }
			  }
			}
			""");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains("Discord.Token"));
	}

	[Fact]
	public void TryLoad_MissingProvider() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "AI": {
			    "OpenAI": { "ApiKey": "k", "BaseUrl": "https://api.example.com/v1", "Model": "m1" }
			  }
			}
			""");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains("AI.Provider"));
	}

	[Fact]
	public void TryLoad_InvalidProvider() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "AI": {
			    "Provider": "claude",
			    "OpenAI": { "ApiKey": "k", "BaseUrl": "https://api.example.com/v1", "Model": "m1" }
			  }
			}
			""");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains("AI.Provider 无效"));
	}

	[Fact]
	public void TryLoad_ProviderOpenAIButSectionMissing() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "AI": { "Provider": "openai" }
			}
			""");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains("AI.OpenAI 节未配置"));
	}

	[Fact]
	public void TryLoad_ProviderGeminiButSectionMissing() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "AI": { "Provider": "gemini" }
			}
			""");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains("AI.Gemini 节未配置"));
	}

	[Fact]
	public void TryLoad_GeminiAuthConflict() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "AI": {
			    "Provider": "gemini",
			    "Gemini": { "ApiKey": "gk", "Project": "p", "Location": "l", "Model": "gm" }
			  }
			}
			""");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains("只能二选一"));
	}

	[Fact]
	public void TryLoad_GeminiAuthIncomplete() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "AI": {
			    "Provider": "gemini",
			    "Gemini": { "Project": "p", "Model": "gm" }
			  }
			}
			""");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains("鉴权不完整"));
	}

	[Fact]
	public void TryLoad_GeminiTouchedWithoutModel() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "AI": {
			    "Provider": "openai",
			    "OpenAI": { "ApiKey": "k", "BaseUrl": "https://api.example.com/v1", "Model": "m1" },
			    "Gemini": { "ApiKey": "gk" }
			  }
			}
			""");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains("AI.Gemini.Model"));
	}

	[Fact]
	public void TryLoad_MaxParallelZero() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "AI": {
			    "Provider": "openai",
			    "MaxParallel": 0,
			    "OpenAI": { "ApiKey": "k", "BaseUrl": "https://api.example.com/v1", "Model": "m1" }
			  }
			}
			""");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains("AI.MaxParallel"));
	}

	[Fact]
	public void TryLoad_InvalidBaseUrl() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "tok" },
			  "AI": {
			    "Provider": "openai",
			    "OpenAI": { "ApiKey": "k", "BaseUrl": "not-a-url", "Model": "m1" }
			  }
			}
			""");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains("AI.OpenAI.BaseUrl"));
	}

	[Fact]
	public void TryLoad_ReportsAllErrorsAtOnce() {
		using var cfg = new TempConfigFile("""
			{
			  "Discord": { "Token": "" },
			  "AI": {
			    "Provider": "openai",
			    "MaxParallel": -1,
			    "OpenAI": { "ApiKey": "k" }
			  }
			}
			""");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.True(errors.Count >= 4, $"expected >= 4 errors, got {errors.Count}");
	}

	[Fact]
	public void TryLoad_FailureKeepsPreviousConfig() {
		using var first = new TempConfigFile(ValidOpenAI);
		Assert.True(AppConfig.TryLoad(first.Path, out _));
		Assert.Equal("m1", AppConfig.AI.OpenAI.Model);

		using var broken = new TempConfigFile("""
			{
			  "Discord": { "Token": "" },
			  "AI": { "Provider": "openai" }
			}
			""");
		Assert.False(AppConfig.TryLoad(broken.Path, out _));
		Assert.Equal("m1", AppConfig.AI.OpenAI.Model);
		Assert.Equal("tok-1", AppConfig.Discord.Token);
	}

	[Fact]
	public void TryLoad_NullRoot_FailsWithExplicitError() {
		using var cfg = new TempConfigFile("null");
		Assert.False(AppConfig.TryLoad(cfg.Path, out var errors));
		Assert.Contains(errors, static e => e.Contains("根节点必须为 JSON 对象"));
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
		Assert.Contains(errors, static e => e.Contains("AI.Provider"));
	}

	[Fact]
	public void TryLoad_NullRootKeepsPreviousConfig() {
		using var first = new TempConfigFile(ValidOpenAI);
		Assert.True(AppConfig.TryLoad(first.Path, out _));

		using var broken = new TempConfigFile("null");
		Assert.False(AppConfig.TryLoad(broken.Path, out var errors));
		Assert.NotEmpty(errors);
		Assert.Equal("m1", AppConfig.AI.OpenAI.Model);
		Assert.Equal("tok-1", AppConfig.Discord.Token);
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
