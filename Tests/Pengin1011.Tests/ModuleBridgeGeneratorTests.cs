using Microsoft.CodeAnalysis;

namespace Pengin1011.Tests;

public sealed class ModuleBridgeGeneratorTests {
	private const string ExportAttribute = "namespace Pengin1011.Core { [System.AttributeUsage(System.AttributeTargets.Method)] public sealed class ModuleBridgeExportAttribute : System.Attribute { } }";

	private const string RuntimeInterface = "namespace Pengin1011.Core.Modules { public interface IModuleRuntime { System.Threading.Tasks.Task InitializeAsync(System.Threading.CancellationToken ct); System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken ct); } }";

	private const string ProviderStubs = """
namespace Pengin1011.Core {
	public sealed class ModuleBridgeRegistrar { public void Add(string name, System.Delegate handler) { } }
	public interface IModuleBridgeProvider { void Register(ModuleBridgeRegistrar registrar, Pengin1011.Core.Modules.IModuleRuntime runtime); }
	[System.AttributeUsage(System.AttributeTargets.Assembly)]
	public sealed class ModuleBridgeProviderAttribute : System.Attribute {
		public ModuleBridgeProviderAttribute(System.Type providerType) { ProviderType = providerType; }
		public System.Type ProviderType { get; }
	}
}
""";

	private static string Host(params string[] declarations) {
		return string.Join("\n", ExportAttribute, RuntimeInterface, ProviderStubs, string.Join("\n", declarations));
	}

	private static GeneratorOutcome Run(string source, params (string HintName, string Text)[] additional) {
		Assert.True(GeneratorTestHost.HasFrameworkReferences, "BCL 引用缺失，生成器测试无法运行");
		return GeneratorTestHost.Run(source, additionalTexts: additional);
	}

	[Fact]
	public void EscapedIdentifier_MethodName_GeneratesCallableProvider() {
		var outcome = Run(Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static string @event() => "ok";
}
"""));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		Assert.Contains("@event", outcome.SourceContaining("__ModuleBridgeProvider"));
	}

	[Fact]
	public void EscapedIdentifier_ParameterName_GeneratesCompilableProxy() {
		var source = Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static string Echo(string @event) => @event;
}
""");
		var outcome = Run(source, ("ModuleBridgeManifest.g.cs", ManifestFor(source, "EscapedParam")));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		Assert.Contains("@event", outcome.SourceContaining("class EscapedParam"));
	}

	[Fact]
	public void ParamsParameter_KeepsCallSiteExpansionOnTheProxy() {
		var source = Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static int Count(params string[] items) => items.Length;
}
""");
		var outcome = Run(source, ("ModuleBridgeManifest.g.cs", ManifestFor(source, "ParamModule")));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		var proxy = outcome.SourceContaining("class ParamModule");
		Assert.Contains("params string[] items", proxy);
		Assert.Contains("p:items:string[]", outcome.SourceContaining("__ModuleBridgeManifest"));
	}

	[Fact]
	public void NestedGenericContainingType_KeepsOuterTypeArguments() {
		var outcome = Run(Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static System.Collections.Generic.Dictionary<int, string>.KeyCollection Keys() => null!;
}
"""));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		Assert.Contains("Dictionary<int, string>.KeyCollection", outcome.SourceContaining("__ModuleBridgeManifest"));
	}

	[Fact]
	public void NestedGenericNullability_IsPreserved() {
		var outcome = Run(Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int?>>[]? Table() => null;
}
"""));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		var manifest = outcome.SourceContaining("__ModuleBridgeManifest");
		Assert.Contains("int?", manifest);
		Assert.Contains("[]?", manifest);
	}

	[Theory]
	[InlineData("System.Collections.Generic.IEnumerator<int>")]
	[InlineData("System.Collections.Generic.List<int>.Enumerator")]
	[InlineData("System.Collections.Generic.IAsyncEnumerator<int>")]
	[InlineData("System.Collections.IEnumerable")]
	[InlineData("System.Linq.IQueryable<int>")]
	[InlineData("System.Linq.EnumerableQuery<int>")]
	[InlineData("System.Linq.IOrderedEnumerable<int>")]
	[InlineData("System.IObservable<int>")]
	[InlineData("System.Threading.Tasks.ValueTask<int>?")]
	public void UnsupportedDeferredReturn_ReportsPbg010(string returnType) {
		var outcome = Run(Host($$"""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static {{returnType}} Walk() => default!;
}
"""));

		GeneratorTestHost.Single(outcome, "PBG010");
	}

	[Theory]
	[InlineData("System.Threading.Tasks.Task")]
	[InlineData("System.Threading.Tasks.Task<int>")]
	[InlineData("System.Threading.Tasks.ValueTask")]
	[InlineData("System.Threading.Tasks.ValueTask<int>")]
	[InlineData("System.Collections.Generic.IEnumerable<int>")]
	[InlineData("System.Collections.Generic.IAsyncEnumerable<int>")]
	public void DeferredReturn_WithRefLikeParameter_ReportsPbg010(string returnType) {
		foreach (var parameterType in new[] { "System.Span<int>", "System.ReadOnlySpan<int>" }) {
			var outcome = Run(Host($$"""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static {{returnType}} Value({{parameterType}} items) => default!;
}
"""));

			GeneratorTestHost.Single(outcome, "PBG010");
			GeneratorTestHost.AssertCompiles(outcome);
			Assert.DoesNotContain(outcome.GeneratedSources, source => source.HintName == "ModuleBridgeProvider.g.cs");
			Assert.Contains("__ExportCount = 0;", outcome.SourceContaining("__ModuleBridgeManifest"));
		}
	}

	[Theory]
	[InlineData("System.Span<int>")]
	[InlineData("System.ReadOnlySpan<int>")]
	public void SynchronousReturn_WithRefLikeParameter_ProviderAndProxyCompile(string parameterType) {
		var source = Host($$"""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static int Count({{parameterType}} items) => items.Length;
	[Pengin1011.Core.ModuleBridgeExport]
	public static void Accept({{parameterType}} items) { }
}
""");
		var outcome = Run(source, ("ModuleBridgeManifest.g.cs", ManifestFor(source, "SpanModule")));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		Assert.Contains(outcome.GeneratedSources, generated => generated.HintName == "ModuleBridge.SpanModule.g.cs");
	}

	[Theory]
	[InlineData("System.Threading.Tasks.TaskFactory", "System.Threading.Tasks.TaskFactory")]
	[InlineData("int[]", "int[]")]
	[InlineData("System.Collections.Generic.List<int>", "List<int>")]
	public void OrdinaryReturnSharingDeferredTypeNames_RemainsAValue(string returnType, string expectedType) {
		var source = Host($$"""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static {{returnType}} Value() => default!;
}
""");
		var outcome = Run(source, ("ModuleBridgeManifest.g.cs", ManifestFor(source, "ValueModule")));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		var proxy = outcome.SourceByHint("ModuleBridge.ValueModule.g.cs");
		Assert.Contains(expectedType, proxy);
		Assert.DoesNotContain("ModuleBridgeLeaseScope.Run", proxy);
	}

	[Theory]
	[InlineData("System.Threading.Tasks.Task<System.Collections.Generic.IEnumerable<int>>")]
	[InlineData("System.Threading.Tasks.ValueTask<System.Collections.Generic.IAsyncEnumerable<int>>")]
	[InlineData("System.Threading.Tasks.Task<System.Threading.Tasks.Task<int>>")]
	[InlineData("System.Collections.Generic.IEnumerable<System.Threading.Tasks.Task<int>>")]
	[InlineData("System.Collections.Generic.IEnumerable<System.Collections.Generic.IEnumerable<int>>")]
	[InlineData("System.Collections.Generic.IAsyncEnumerable<System.Threading.Tasks.ValueTask<int>>")]
	[InlineData("System.Collections.Generic.IEnumerable<int>[]")]
	[InlineData("System.Threading.Tasks.Task<int>[]")]
	[InlineData("System.Collections.Generic.List<System.Threading.Tasks.Task<int>>")]
	[InlineData("System.Collections.Generic.List<System.Collections.Generic.IEnumerable<int>>")]
	[InlineData("System.Collections.Generic.Dictionary<string, System.Threading.Tasks.Task<int>[]>")]
	[InlineData("System.Collections.Generic.Dictionary<System.Threading.Tasks.Task<int>, int>.KeyCollection")]
	[InlineData("System.Threading.Tasks.Task<System.Collections.Generic.List<System.Threading.Tasks.ValueTask<int>?>>")]
	[InlineData("System.Collections.Generic.IEnumerable<System.Collections.Generic.IEnumerator<int>>")]
	[InlineData("System.Threading.Tasks.Task<System.Linq.IQueryable<int>>")]
	[InlineData("System.Collections.Generic.List<System.Linq.IOrderedEnumerable<int>>")]
	[InlineData("System.Threading.Tasks.ValueTask<System.IObservable<int>>")]
	[InlineData("System.Collections.Generic.List<System.Collections.IEnumerable>")]
	[InlineData("System.Threading.Tasks.Task<System.Collections.Generic.List<int>.Enumerator>")]
	[InlineData("System.Collections.Generic.List<System.Collections.Generic.List<int>.Enumerator>")]
	[InlineData("System.Threading.Tasks.Task<System.Linq.EnumerableQuery<int>>")]
	[InlineData("System.Threading.Tasks.ValueTask<System.Collections.Generic.List<int>.Enumerator>")]
	[InlineData("System.Collections.Generic.IEnumerable<System.Linq.EnumerableQuery<int>>")]
	[InlineData("System.Linq.EnumerableQuery<int>[]")]
	[InlineData("System.Collections.Generic.List<System.Collections.Generic.List<int>.Enumerator?>")]
	public void NestedDeferredReturn_ReportsPbg010WithoutGeneratingProvider(string returnType) {
		var outcome = Run(Host($$"""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static {{returnType}} Walk() => default!;
}
"""));

		GeneratorTestHost.Single(outcome, "PBG010");
		GeneratorTestHost.AssertCompiles(outcome);
		Assert.DoesNotContain(outcome.GeneratedSources, source => source.HintName == "ModuleBridgeProvider.g.cs");
		Assert.Contains("__ExportCount = 0;", outcome.SourceContaining("__ModuleBridgeManifest"));
	}

	[Theory]
	[InlineData("int")]
	[InlineData("string")]
	[InlineData("int[]")]
	[InlineData("System.Collections.Generic.List<int>")]
	[InlineData("System.Threading.Tasks.Task")]
	[InlineData("System.Threading.Tasks.Task<int>")]
	[InlineData("System.Threading.Tasks.Task<System.Collections.Generic.List<int>>")]
	[InlineData("System.Threading.Tasks.ValueTask")]
	[InlineData("System.Threading.Tasks.ValueTask<int>")]
	[InlineData("System.Threading.Tasks.ValueTask<int[]>")]
	[InlineData("System.Collections.Generic.IEnumerable<int>")]
	[InlineData("System.Collections.Generic.IAsyncEnumerable<int>")]
	[InlineData("System.Collections.Generic.IEnumerable<System.Collections.Generic.List<int>>")]
	[InlineData("System.Threading.Tasks.Task<System.Collections.Generic.Dictionary<int, string>.KeyCollection>")]
	public void SupportedReturnShapes_ProviderAndProxyCompile(string returnType) {
		var source = Host($$"""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static {{returnType}} Value() => default!;
}
""");
		var outcome = Run(source, ("ModuleBridgeManifest.g.cs", ManifestFor(source, "ValueModule")));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		Assert.Contains(outcome.GeneratedSources, generated => generated.HintName == "ModuleBridge.ValueModule.g.cs");
	}

	[Fact]
	public void ProxyLocalNames_DoNotCollideWithExportParameters() {
		var source = Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static string Value(int call, string handler) => handler + call;
	[Pengin1011.Core.ModuleBridgeExport]
	public static System.Collections.Generic.IEnumerable<string> Sequence(int call, string handler) => new[] { handler + call };
}
""");
		var outcome = Run(source, ("ModuleBridgeManifest.g.cs", ManifestFor(source, "LocalNames")));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
	}

	[Fact]
	public void BclReturnTypes_AreAccepted() {
		var outcome = Run(Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static decimal Amount() => 0;
	[Pengin1011.Core.ModuleBridgeExport]
	public static System.Guid Id() => default;
	[Pengin1011.Core.ModuleBridgeExport]
	public static System.Threading.Tasks.ValueTask<int> Later() => default;
	[Pengin1011.Core.ModuleBridgeExport]
	public static System.Collections.Generic.IReadOnlyDictionary<string, long[]> Map() => null!;
}
"""));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
	}

	[Fact]
	public void HostTypes_AreAccepted() {
		var outcome = Run(Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static Pengin1011.Core.Modules.ModuleState Current() => Pengin1011.Core.Modules.ModuleState.Ready;
	[Pengin1011.Core.ModuleBridgeExport]
	public static string Describe(Pengin1011.Core.Modules.ModuleState state) => state.ToString();
}
"""));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		Assert.Contains("ModuleState", outcome.SourceContaining("__ModuleBridgeManifest"));
	}

	[Fact]
	public void NonPublicMethod_ReportsPbg001() {
		var outcome = Run(Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	internal static string Hidden() => "x";
}
"""));

		Assert.Equal("PBG001", GeneratorTestHost.Single(outcome, "PBG001").Id);
	}

	[Fact]
	public void NonPublicType_ReportsPbg002() {
		var outcome = Run(Host("""
internal static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static string Hidden() => "x";
}
"""));

		Assert.Equal("PBG002", GeneratorTestHost.Single(outcome, "PBG002").Id);
	}

	[Fact]
	public void NonPublicEnclosingType_ReportsPbg002() {
		var outcome = Run(Host("""
internal static class Outer {
	public static class Exports {
		[Pengin1011.Core.ModuleBridgeExport]
		public static string Nested() => "x";
	}
}
"""));

		Assert.Equal("PBG002", GeneratorTestHost.Single(outcome, "PBG002").Id);
	}

	[Fact]
	public void InstanceExportOutsideRuntime_ReportsPbg003() {
		var outcome = Run(Host("""
public sealed class NotTheRuntime {
	[Pengin1011.Core.ModuleBridgeExport]
	public string Value() => "x";
}
public sealed class OnlyRuntime : Pengin1011.Core.Modules.IModuleRuntime {
	public System.Threading.Tasks.Task InitializeAsync(System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
	public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
}
"""));

		Assert.Equal("PBG003", GeneratorTestHost.Single(outcome, "PBG003").Id);
	}

	[Fact]
	public void GenericDeclaringType_ReportsPbg004() {
		var outcome = Run(Host("""
public static class Exports<T> {
	[Pengin1011.Core.ModuleBridgeExport]
	public static string Value() => "x";
}
"""));

		Assert.Equal("PBG004", GeneratorTestHost.Single(outcome, "PBG004").Id);
	}

	[Fact]
	public void GenericMethod_ReportsPbg005() {
		var outcome = Run(Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static string Value<T>() => "x";
}
"""));

		Assert.Equal("PBG005", GeneratorTestHost.Single(outcome, "PBG005").Id);
	}

	[Fact]
	public void RefParameter_ReportsPbg006() {
		var outcome = Run(Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static void Update(ref int value) { }
}
"""));

		Assert.Equal("PBG006", GeneratorTestHost.Single(outcome, "PBG006").Id);
	}

	[Fact]
	public void RefReturn_ReportsPbg006() {
		var outcome = Run(Host("""
public static class Exports {
	private static int _slot;
	[Pengin1011.Core.ModuleBridgeExport]
	public static ref int Slot() => ref _slot;
}
"""));

		Assert.Equal("PBG006", GeneratorTestHost.Single(outcome, "PBG006").Id);
	}

	[Fact]
	public void RefReadonlyReturn_ReportsPbg006() {
		var outcome = Run(Host("""
public static class Exports {
	private static int _slot;
	[Pengin1011.Core.ModuleBridgeExport]
	public static ref readonly int Slot() => ref _slot;
}
"""));

		Assert.Equal("PBG006", GeneratorTestHost.Single(outcome, "PBG006").Id);
	}

	[Fact]
	public void OptionalParameter_ReportsPbg007() {
		var outcome = Run(Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static string Value(int count = 1) => count.ToString();
}
"""));

		Assert.Equal("PBG007", GeneratorTestHost.Single(outcome, "PBG007").Id);
	}

	[Fact]
	public void ModulePrivateType_ReportsPbg008() {
		var outcome = Run(Host("""
public sealed class PrivatePayload { public int Value { get; set; } }
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static void Send(PrivatePayload payload) { }
}
"""));

		var diagnostic = GeneratorTestHost.Single(outcome, "PBG008");
		Assert.Contains("PrivatePayload", diagnostic.GetMessage());
	}

	[Fact]
	public void TooManyParameters_ReportsPbg009() {
		var parameters = string.Join(", ", Enumerable.Range(0, 17).Select(index => "int p" + index));
		var outcome = Run(Host($$"""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static void Wide({{parameters}}) { }
}
"""));

		var diagnostic = GeneratorTestHost.Single(outcome, "PBG009");
		Assert.Contains("17", diagnostic.GetMessage());
	}

	[Fact]
	public void ForeignModuleType_ReportsPbg010() {
		var foreign = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
			"OtherModule",
			[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("namespace OtherModule { public sealed class Foreign { public int Value { get; set; } } }")],
			GeneratorTestHost.ReferenceSet(),
			new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
		using var stream = new MemoryStream();
		var emitted = foreign.Emit(stream);
		Assert.True(emitted.Success, string.Join(" | ", emitted.Diagnostics.Select(d => d.GetMessage())));
		var reference = MetadataReference.CreateFromImage(stream.ToArray());

		var source = Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static void Send(OtherModule.Foreign payload) { }
}
""");
		var outcome = GeneratorTestHost.Run(source, "TestModule", reference);

		var diagnostic = GeneratorTestHost.Single(outcome, "PBG010");
		Assert.Contains("Foreign", diagnostic.GetMessage());
	}

	[Fact]
	public void TupleReturn_ReportsPbg010() {
		var outcome = Run(Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static (int a, string b) Pair() => default;
}
"""));

		Assert.Equal("PBG010", GeneratorTestHost.Single(outcome, "PBG010").Id);
	}

	[Fact]
	public void MultidimensionalArray_ReportsPbg010() {
		var outcome = Run(Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static int[,] Grid() => null!;
}
"""));

		Assert.Equal("PBG010", GeneratorTestHost.Single(outcome, "PBG010").Id);
	}

	[Fact]
	public void DuplicateExportName_ReportsPbg011() {
		var outcome = Run(Host("""
public static class First {
	[Pengin1011.Core.ModuleBridgeExport]
	public static string Value() => "a";
}
public static class Second {
	[Pengin1011.Core.ModuleBridgeExport]
	public static string Value() => "b";
}
"""));

		var diagnostic = GeneratorTestHost.Single(outcome, "PBG011");
		Assert.Contains("Value", diagnostic.GetMessage());
	}

	[Fact]
	public void InvalidModuleName_ReportsPbg012() {
		var source = Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static string Value() => "a";
}
""");
		var outcome = GeneratorTestHost.Run(source, "Bad.Module");

		Assert.Equal("PBG012", GeneratorTestHost.Single(outcome, "PBG012").Id);
	}

	[Fact]
	public void ExportNamedLikeModule_ReportsPbg013NotBrokenCode() {
		var source = Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static string SameName() => "a";
}
""");
		var outcome = GeneratorTestHost.Run(source, "SameName");

		var diagnostic = GeneratorTestHost.Single(outcome, "PBG013");
		Assert.Contains("SameName", diagnostic.GetMessage());
	}

	[Fact]
	public void ModuleNamedModuleBridge_ReportsPbg013() {
		var source = Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static string Value() => "a";
}
""");
		var outcome = GeneratorTestHost.Run(source, "ModuleBridge");

		Assert.Equal("PBG013", GeneratorTestHost.Single(outcome, "PBG013").Id);
	}

	[Fact]
	public void ManifestEntryCountMismatch_ReportsPbg104() {
		var manifest = """
namespace Pengin1011.Core {
	internal static class __ModuleBridgeManifest {
		public const string __Format = "pengin1011.modulebridge.manifest/3";
		public const string __Module = "Dep";
		public const int __ExportCount = 5;
		public const string __Export_0 = "Value|static|string|";
	}
}
""";
		var outcome = Run(Host("public static class Local { }"), ("ModuleBridgeManifest.g.cs", manifest));

		var diagnostic = GeneratorTestHost.Single(outcome, "PBG104");
		Assert.Contains("5", diagnostic.GetMessage());
	}

	[Fact]
	public void LegacyManifestFormat_ReportsPbg103() {
		var manifest = """
namespace Pengin1011.Core {
	internal static class __ModuleBridgeManifest {
		public const string __Format = "pengin1011.modulebridge.manifest/1";
		public const string __Module = "Dep";
		public const int __ExportCount = 1;
		public const string __Export_0 = "Value|static|string|";
	}
}
""";
		var outcome = Run(Host("public static class Local { }"), ("ModuleBridgeManifest.g.cs", manifest));

		var diagnostic = GeneratorTestHost.Single(outcome, "PBG103");
		Assert.Contains("Dep", diagnostic.GetMessage());
	}

	[Fact]
	public void DuplicateModuleManifest_ReportsPbg105() {
		var manifest = """
namespace Pengin1011.Core {
	internal static class __ModuleBridgeManifest {
		public const string __Format = "pengin1011.modulebridge.manifest/3";
		public const string __Module = "Dep";
		public const int __ExportCount = 1;
		public const string __Export_0 = "Value|static|string|";
	}
}
""";
		var outcome = Run(Host("public static class Local { }"), ("ModuleBridgeManifest.g.cs", manifest), ("second/ModuleBridgeManifest.g.cs", manifest));

		var diagnostic = GeneratorTestHost.Single(outcome, "PBG105");
		Assert.Contains("Dep", diagnostic.GetMessage());
	}

	[Fact]
	public void DuplicateExportInManifest_ReportsPbg106() {
		var manifest = """
namespace Pengin1011.Core {
	internal static class __ModuleBridgeManifest {
		public const string __Format = "pengin1011.modulebridge.manifest/3";
		public const string __Module = "Dep";
		public const int __ExportCount = 2;
		public const string __Export_0 = "Value|static|string|";
		public const string __Export_1 = "Value|static|string|";
	}
}
""";
		var outcome = Run(Host("public static class Local { }"), ("ModuleBridgeManifest.g.cs", manifest));

		var diagnostic = GeneratorTestHost.Single(outcome, "PBG106");
		Assert.Contains("Value", diagnostic.GetMessage());
	}

	[Fact]
	public void MalformedManifestEntry_ReportsPbg101() {
		var manifest = """
namespace Pengin1011.Core {
	internal static class __ModuleBridgeManifest {
		public const string __Format = "pengin1011.modulebridge.manifest/3";
		public const string __Module = "Dep";
		public const int __ExportCount = 1;
		public const string __Export_0 = "Value|static|string|BAD";
	}
}
""";
		var outcome = Run(Host("public static class Local { }"), ("ModuleBridgeManifest.g.cs", manifest));

		Assert.Equal("PBG101", GeneratorTestHost.Single(outcome, "PBG101").Id);
	}

	[Fact]
	public void MalformedManifestModuleName_ReportsPbg102() {
		var manifest = """
namespace Pengin1011.Core {
	internal static class __ModuleBridgeManifest {
		public const string __Format = "pengin1011.modulebridge.manifest/3";
		public const string __Module = "Dep.Module";
		public const int __ExportCount = 1;
		public const string __Export_0 = "Value|static|string|";
	}
}
""";
		var outcome = Run(Host("public static class Local { }"), ("ModuleBridgeManifest.g.cs", manifest));

		Assert.Equal("PBG102", GeneratorTestHost.Single(outcome, "PBG102").Id);
	}

	[Fact]
	public void EmptyManifest_GeneratesUsableProxyWithoutDiagnostics() {
		var source = Host("public static class NoExports { }");
		var outcome = Run(source);

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		var manifest = outcome.SourceContaining("__ModuleBridgeManifest");
		Assert.Contains("__ExportCount = 0", manifest);
		Assert.DoesNotContain("__Export_0", manifest);
	}

	[Fact]
	public void RecordClassRuntime_ScansInstanceExports() {
		var outcome = Run(Host("""
public sealed record Runtime : Pengin1011.Core.Modules.IModuleRuntime {
	[Pengin1011.Core.ModuleBridgeExport]
	public string Describe() => "ok";
	public System.Threading.Tasks.Task InitializeAsync(System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
	public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
}
"""));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		Assert.Contains("Describe", outcome.SourceContaining("__ModuleBridgeProvider"));
	}

	[Fact]
	public void UnrelatedAttributeWithSameShortName_IsNotTreatedAsAnExport() {
		var source = string.Join("\n", ExportAttribute, RuntimeInterface, """
namespace Other { [System.AttributeUsage(System.AttributeTargets.Method)] public sealed class ModuleBridgeExportAttribute : System.Attribute { } }
public static class Exports {
	[Other.ModuleBridgeExport]
	public static string Sneaky() => "x";
}
""");
		var outcome = Run(source);

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		Assert.DoesNotContain("Sneaky", outcome.SourceContaining("__ModuleBridgeManifest"));
	}

	[Fact]
	public void SourceFixture_UsedByRefReturnCase_IsItselfValid() {
		var source = Host("""
public static class Exports {
	private static int _slot;
	[Pengin1011.Core.ModuleBridgeExport]
	public static ref int Slot() => ref _slot;
}
""");
		var outcome = Run(source);

		Assert.DoesNotContain(outcome.CompilationDiagnostics, d => d.Id == "CS0103" || d.Id == "CS0117");
	}

	[Fact]
	public void KeywordNamedExport_IsEscapedInGeneratedCodeButRawInTheManifest() {
		var source = Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static void @event(string @class) { }
}
""");
		var outcome = Run(source);

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		var manifest = outcome.SourceContaining("__ModuleBridgeManifest");
		Assert.Contains("event|static|void|-:class:string", manifest);
		var provider = outcome.SourceContaining("__ModuleBridgeProvider");
		Assert.Contains(".@event", provider);
		Assert.Contains("registrar.Add(\"event\"", provider);
	}

	[Fact]
	public void KeywordNamedExport_SurvivesTheProviderToConsumerRoundTrip() {
		var providerSource = Host("""
public static class Exports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static void @event(string @class) { }
}
""");
		var providerRun = GeneratorTestHost.Run(providerSource, "KeywordModule");
		GeneratorTestHost.AssertNoGeneratorDiagnostics(providerRun);
		var manifest = providerRun.SourceContaining("__ModuleBridgeManifest");

		var consumerSource = Host("""
public static class Consumer {
	public static void Call() => Pengin1011.Core.ModuleBridge.KeywordModule.@event("x");
}
""");
		var consumerRun = GeneratorTestHost.Run(consumerSource, "ConsumerModule", ("ModuleBridgeManifest.g.cs", manifest));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(consumerRun);
		GeneratorTestHost.AssertCompiles(consumerRun);
	}

	[Fact]
	public void CorruptedManifestFormat_ReportsPbg103RatherThanBeingIgnored() {
		var manifest = """
namespace Pengin1011.Core {
	internal static class __ModuleBridgeManifest {
		public const string __Format = "pengin1011.modulebridge.manifest/999";
		public const string __Module = "Dep";
		public const int __ExportCount = 1;
		public const string __Export_0 = "Value|static|string|";
	}
}
""";
		var outcome = Run(Host("public static class Local { }"), ("ModuleBridgeManifest.g.cs", manifest));

		var diagnostic = GeneratorTestHost.Single(outcome, "PBG103");
		Assert.Contains("Dep", diagnostic.GetMessage());
	}

	[Fact]
	public void ManifestMissingItsModuleName_ReportsPbg102() {
		var manifest = """
namespace Pengin1011.Core {
	internal static class __ModuleBridgeManifest {
		public const string __Format = "pengin1011.modulebridge.manifest/3";
		public const int __ExportCount = 0;
	}
}
""";
		var outcome = Run(Host("public static class Local { }"), ("ModuleBridgeManifest.g.cs", manifest));

		Assert.Equal("PBG102", GeneratorTestHost.Single(outcome, "PBG102").Id);
	}

	private static string ManifestFor(string source, string module) {
		var exports = ExtractExports(source);
		var lines = exports.Select((name, index) => $"\t\tpublic const string __Export_{index} = \"{name}\";");
		return $$"""
namespace Pengin1011.Core {
	internal static class __ModuleBridgeManifest {
		public const string __Format = "pengin1011.modulebridge.manifest/3";
		public const string __Module = "{{module}}";
		public const int __ExportCount = {{exports.Count}};
{{string.Join("\n", lines)}}
	}
}
""";
	}

	private static List<string> ExtractExports(string source) {
		var outcome = GeneratorTestHost.Run(source, "ExportSource");
		var manifest = outcome.SourceContaining("__ModuleBridgeManifest");
		var lines = new List<string>();
		for (var index = 0; ; index++) {
			var marker = $"__Export_{index} = \"";
			var start = manifest.IndexOf(marker, StringComparison.Ordinal);
			if (start < 0) break;
			start += marker.Length;
			var end = manifest.IndexOf('"', start);
			lines.Add(manifest.Substring(start, end - start));
		}
		return lines;
	}
}
