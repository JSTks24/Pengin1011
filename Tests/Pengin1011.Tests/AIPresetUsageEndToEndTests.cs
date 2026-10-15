using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

public sealed class AIPresetUsageEndToEndTests {
	private sealed class FakeSource;

	private const string ModuleSource = """
		using Pengin1011.Services.AI;
		namespace TestModule;
		public static class Caller {
			public static void Go() {
				_ = AIClient.CompleteAsync("absent", new AIRequest { Messages = [] });
			}
		}
		""";

	private static string TempErrorLog() {
		return Path.Combine(Path.GetTempPath(), $"qq1011_e2e_{Guid.NewGuid():N}", "error.log");
	}

	private static System.Reflection.Assembly CompileModule(string source) {
		Assert.True(GeneratorTestHost.HasFrameworkReferences, "BCL 引用缺失，端到端测试无法运行");
		var outcome = GeneratorTestHost.RunAIPreset(source, "TestModule");
		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		var generated = outcome.SourceByHint("AIPresetUsage.g.cs");
		var tree = CSharpSyntaxTree.ParseText(generated);
		var compilation = CSharpCompilation.Create(
			"TestModuleUsage",
			[tree],
			GeneratorTestHost.ReferenceSet(),
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
		using var stream = new MemoryStream();
		var result = compilation.Emit(stream);
		Assert.True(result.Success, string.Join(" | ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
		return System.Reflection.Assembly.Load(stream.ToArray());
	}

	[Fact]
	public void GeneratedManifest_RegistersTheCallSite() {
		AIPresetUsageRegistry.ForgetAll();
		try {
			AIPresetUsageRegistry.Register(CompileModule(ModuleSource), "TestModule");

			var usage = Assert.Single(AIPresetUsageRegistry.Usages);
			Assert.Equal("TestModule", usage.Owner);
			Assert.Equal("TestModule.Caller#Go", usage.CallSite);
			Assert.Equal("absent", usage.Dependency.Name);
			Assert.Equal(["TestModule: TestModule.Caller#Go"], AIPresetUsageRegistry.ReferencesTo("absent"));
		} finally {
			AIPresetUsageRegistry.ForgetAll();
		}
	}

	[Fact]
	public void MissingPreset_FailsStartupCheckAndLogsEveryFailure() {
		var logPath = TempErrorLog();
		Pengin1011.Core.Logging.Logger.SetLogPathForTest(logPath);
		try {
			AIPresetUsageRegistry.Register(CompileModule(ModuleSource), "TestModule");
			AIPresetStore.LoadForTest(new AIPresetDocument { Presets = [new AIPreset {
				Name = "prod",
				Provider = AIPresetProvider.OpenAI,
				ApiKey = "k",
				BaseUrl = "https://api.example.com/v1",
				Model = "m",
			}] });

			Assert.False(AIPresetValidator.TryValidate(out var failures));

			var failure = Assert.Single(failures);
			Assert.Contains("TestModule", failure);
			Assert.Contains("TestModule.Caller#Go", failure);
			Assert.Contains("absent", failure);
			Assert.Contains("absent", File.ReadAllText(logPath));
		} finally {
			Pengin1011.Core.Logging.Logger.SetLogPathForTest(null);
			AIPresetStore.ResetForTest();
			AIPresetUsageRegistry.ForgetAll();
			var directory = Path.GetDirectoryName(logPath)!;
			if (Directory.Exists(directory)) Directory.Delete(directory, true);
		}
	}

	[Fact]
	public void ReferencedPreset_CannotBeDeletedOnceModuleIsRegistered() {
		AIPresetUsageRegistry.ForgetAll();
		try {
			AIPresetUsageRegistry.Register(CompileModule(ModuleSource), "TestModule");
			AIPresetStore.LoadForTest(new AIPresetDocument { Presets = [
				new AIPreset { Name = "absent", Provider = AIPresetProvider.OpenAI, ApiKey = "k", BaseUrl = "https://api.example.com/v1", Model = "m" },
			] });

			Assert.False(AIPresetStore.RemovePatch("absent", null, out var result));
			Assert.Contains(L.Prefix("AIPresetInUse"), result.Error);
			Assert.Contains("TestModule.Caller#Go", result.Error);
			Assert.NotNull(AIPresetStore.Find("absent"));
		} finally {
			AIPresetStore.ResetForTest();
			AIPresetUsageRegistry.ForgetAll();
		}
	}

	[Fact]
	public void DefaultPresetCall_IsIndexedAsDefaultMarker() {
		AIPresetUsageRegistry.ForgetAll();
		try {
			var assembly = CompileModule("""
				using Pengin1011.Services.AI;
				namespace TestModule;
				public static class Caller {
					public static void Go() {
						_ = AIClient.StreamAsync(new AIRequest { Messages = [] });
					}
				}
				""");
			AIPresetUsageRegistry.Register(assembly, "TestModule");

			var usage = Assert.Single(AIPresetUsageRegistry.Usages);
			Assert.True(usage.Dependency.IsDefault);

			AIPresetStore.LoadForTest(new AIPresetDocument());
			Assert.False(AIPresetValidator.TryValidate(out var failures));
			Assert.Contains(L.Prefix("AIPresetUsageDefaultMissing"), Assert.Single(failures));
		} finally {
			AIPresetStore.ResetForTest();
			AIPresetUsageRegistry.ForgetAll();
		}
	}

	[Fact]
	public void NonConstantPresetName_FailsTheModuleBuild() {
		var outcome = GeneratorTestHost.RunAIPreset("""
			using Pengin1011.Services.AI;
			namespace TestModule;
			public static class Caller {
				public static void Go(string presetName) {
					_ = AIClient.CompleteAsync(presetName, new AIRequest { Messages = [] });
				}
			}
			""", "TestModule");

		GeneratorTestHost.Single(outcome, "PAP001");
		Assert.Empty(outcome.GeneratedSources);
	}
}
