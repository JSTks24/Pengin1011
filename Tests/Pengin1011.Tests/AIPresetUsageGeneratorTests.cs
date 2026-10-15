using Microsoft.CodeAnalysis;

namespace Pengin1011.Tests;

public sealed class AIPresetUsageGeneratorTests {
	private const string Header = """
		using Pengin1011.Services.AI;
		public static class Caller {
			public static AIRequest Req() => new AIRequest { Messages = [] };
		}
		""";

	private static string Source(string body) {
		return $"{Header}\n{body}\n";
	}

	private static GeneratorOutcome Run(string body, string assemblyName = "TestModule") {
		Assert.True(GeneratorTestHost.HasFrameworkReferences, "BCL 引用缺失，生成器测试无法运行");
		return GeneratorTestHost.RunAIPreset(Source(body), assemblyName);
	}

	[Theory]
	[InlineData("System.Func<AIRequest, System.Threading.CancellationToken, System.Threading.Tasks.Task<AIResult?>> call = AIClient.CompleteAsync;")]
	[InlineData("var call = (System.Func<AIRequest, System.Threading.CancellationToken, System.Threading.Tasks.Task<AIResult?>>)AIClient.CompleteAsync;")]
	[InlineData("var call = new System.Func<AIRequest, System.Threading.CancellationToken, System.Threading.Tasks.Task<AIResult?>>(AIClient.CompleteAsync);")]
	[InlineData("System.Func<AIRequest, System.Threading.CancellationToken, System.Collections.Generic.IAsyncEnumerable<AIStreamDelta>> call = AIClient.StreamAsync;")]
	public void StaticDelegateConversion_ReportsPap002(string statement) {
		var outcome = Run($$"""
			public static class Calls {
				public static void Go() {
					{{statement}}
				}
			}
			""");

		GeneratorTestHost.Single(outcome, "PAP002");
		GeneratorTestHost.AssertCompiles(outcome);
	}

	[Fact]
	public void NameOfEntry_DoesNotReportPap002OrRecordAUsage() {
		var outcome = Run("public static class Calls { public static string Name() => nameof(AIClient.CompleteAsync); }");

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		Assert.Empty(outcome.GeneratedSources);
	}

	[Fact]
	public void RequestOnlyCall_RecordsDefaultMarker() {
		var outcome = Run("""
			public static class Calls {
				public static void Go() {
					_ = AIClient.CompleteAsync(Caller.Req());
				}
			}
			""");

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		var text = outcome.SourceByHint("AIPresetUsage.g.cs");
		Assert.Contains("registrar.AddDefault(\"Calls#Go\");", text);
	}

	[Fact]
	public void NamedLiteralCall_RecordsPresetName() {
		var outcome = Run("""
			public static class Calls {
				public static void Go() {
					_ = AIClient.CompleteAsync("prod", Caller.Req());
				}
			}
			""");

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		Assert.Contains("registrar.AddNamed(\"Calls#Go\", \"prod\");", outcome.SourceByHint("AIPresetUsage.g.cs"));
	}

	[Fact]
	public void NamedConstCall_ResolvesConstantValue() {
		var outcome = Run("""
			public static class Calls {
				private const string Preset = "prod";
				public static void Go() {
					_ = AIClient.CompleteAsync(Preset, Caller.Req());
				}
			}
			""");

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		Assert.Contains("registrar.AddNamed(\"Calls#Go\", \"prod\");", outcome.SourceByHint("AIPresetUsage.g.cs"));
	}

	[Fact]
	public void NamedArgumentOutOfOrder_StillRecordsPresetName() {
		var outcome = Run("""
			public static class Calls {
				public static void Go() {
					_ = AIClient.CompleteAsync(request: Caller.Req(), presetName: "prod");
				}
			}
			""");

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		Assert.Contains("\"prod\"", outcome.SourceByHint("AIPresetUsage.g.cs"));
	}

	[Fact]
	public void StreamCall_IsRecorded() {
		var outcome = Run("""
			public static class Calls {
				public static void Go() {
					_ = AIClient.StreamAsync("prod", Caller.Req());
				}
			}
			""");

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		Assert.Contains("registrar.AddNamed(\"Calls#Go\", \"prod\");", outcome.SourceByHint("AIPresetUsage.g.cs"));
	}

	[Fact]
	public void NullLiteralCall_RecordsDefaultMarker() {
		var outcome = Run("""
			public static class Calls {
				public static void Go() {
					_ = AIClient.CompleteAsync(null, Caller.Req());
				}
			}
			""");

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		Assert.Contains("registrar.AddDefault(\"Calls#Go\");", outcome.SourceByHint("AIPresetUsage.g.cs"));
	}

	[Fact]
	public void NonConstantPresetName_ReportsPAP001() {
		var outcome = Run("""
			public static class Calls {
				public static void Go(string name) {
					_ = AIClient.CompleteAsync(name, Caller.Req());
				}
			}
			""");

		var diagnostic = GeneratorTestHost.Single(outcome, "PAP001");
		Assert.Equal("AIPreset", diagnostic.Descriptor.Category);
		Assert.Contains("name", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
		Assert.Empty(outcome.SourceTexts);
	}

	[Fact]
	public void InterpolatedPresetName_ReportsPAP001() {
		var outcome = Run("""
			public static class Calls {
				public static void Go(string name) {
					_ = AIClient.CompleteAsync($"p-{name}", Caller.Req());
				}
			}
			""");

		GeneratorTestHost.Single(outcome, "PAP001");
	}

	[Fact]
	public void SameNamedMethodOnAnotherType_IsNotRecorded() {
		var outcome = Run("""
			public static class Other {
				public static void CompleteAsync(string presetName, AIRequest request) { }
			}
			public static class Calls {
				public static void Go() {
					Other.CompleteAsync("prod", Caller.Req());
				}
			}
			""");

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		Assert.Empty(outcome.GeneratedSources);
	}

	[Fact]
	public void NoCallSites_ProducesNoSource() {
		var outcome = Run("""
			public static class Calls {
				public static void Go() { }
			}
			""");

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		Assert.Empty(outcome.GeneratedSources);
	}

	[Fact]
	public void GeneratedSource_DeclaresAssemblyProviderAndCompiles() {
		var outcome = Run("""
			public static class Calls {
				public static void Go() {
					_ = AIClient.CompleteAsync("prod", Caller.Req());
					_ = AIClient.CompleteAsync(Caller.Req());
				}
			}
			""");

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		var text = outcome.SourceByHint("AIPresetUsage.g.cs");
		Assert.Contains("AIPresetUsageProvider", text);
		Assert.Contains("__AIPresetUsageProvider", text);
		Assert.Contains("IAIPresetUsageProvider", text);
	}

	[Fact]
	public void DuplicateCallSites_AreDeduplicated() {
		var outcome = Run("""
			public static class Calls {
				public static void Go() {
					_ = AIClient.CompleteAsync("prod", Caller.Req());
					_ = AIClient.CompleteAsync("prod", Caller.Req());
				}
			}
			""");

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		var text = outcome.SourceByHint("AIPresetUsage.g.cs");
		var first = text.IndexOf("registrar.Add", StringComparison.Ordinal);
		var last = text.LastIndexOf("registrar.Add", StringComparison.Ordinal);
		Assert.Equal(first, last);
	}

	[Fact]
	public void CallSiteText_NamesContainingTypeAndMethod() {
		var outcome = Run("""
			public static class Calls {
				public static void Go() {
					_ = AIClient.CompleteAsync("prod", Caller.Req());
				}
			}
			""");

		Assert.Contains("Calls#Go", outcome.SourceByHint("AIPresetUsage.g.cs"));
	}

	[Fact]
	public void EscapedPresetName_IsEscapedInGeneratedLiteral() {
		var outcome = Run("""
			public static class Calls {
				public static void Go() {
					_ = AIClient.CompleteAsync("a\"b", Caller.Req());
				}
			}
			""");

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		Assert.Contains("\"a\\\"b\"", outcome.SourceByHint("AIPresetUsage.g.cs"));
	}
}
