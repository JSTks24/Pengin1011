namespace Pengin1011.Tests;

public sealed class ModuleBridgeManifestValidationTests {
	private const string ManifestPath = "dependencies/Dep/ModuleBridgeManifest.g.cs";
	private const string ConsumerSource = "public static class Consumer { }";

	[Theory]
	[InlineData("")]
	[InlineData("namespace Pengin1011.Core { }")]
	[InlineData("namespace Pengin1011.Core { internal static class __ModuleBridgeManifest {")]
	public void ManifestFile_EmptyOrMalformed_ReportsPbg101(string text) {
		AssertRejected(Run(text), "PBG101");
	}

	[Theory]
	[InlineData("pengin1011.modulebridge.manifest/30")]
	[InlineData("pengin1011.modulebridge.manifest/3-extra")]
	[InlineData("pengin1011.modulebridge.manifest/3 ")]
	public void ManifestVersion_RequiresExactMatch(string format) {
		AssertRejected(Run(Manifest(format: format)), "PBG103");
	}

	[Theory]
	[InlineData("")]
	[InlineData("public const int __ExportCount = -1;")]
	[InlineData("public const int __ExportCount = 2147483648;")]
	[InlineData("public const string __ExportCount = \"1\";")]
	public void ManifestCount_MissingOrInvalid_ReportsPbg101(string count) {
		var manifest = Manifest().Replace("public const int __ExportCount = 1;", count, StringComparison.Ordinal);

		AssertRejected(Run(manifest), "PBG101");
	}

	[Theory]
	[InlineData(0, "public const string __Export_0 = \"Value|static|string|\";")]
	[InlineData(2, "public const string __Export_0 = \"Value|static|string|\";")]
	[InlineData(1, "public const string __Export_2 = \"Value|static|string|\";")]
	[InlineData(2, "public const string __Export_0 = \"First|static|string|\";\npublic const string __Export_2 = \"Second|static|string|\";")]
	[InlineData(2, "public const string __Export_0 = \"First|static|string|\";\npublic const string __Export_0 = \"Second|static|string|\";")]
	[InlineData(1, "public const string __Export_bad = \"Value|static|string|\";")]
	[InlineData(1, "public const string __Export_2147483648 = \"Value|static|string|\";")]
	public void ManifestIndices_MustMatchTheDeclaredCount(int count, string entries) {
		AssertRejected(Run(Manifest(count: count, entries: entries)), "PBG104");
	}

	[Theory]
	[InlineData("Value|unknown|string|")]
	[InlineData("Value||string|")]
	[InlineData("Value|static|string|x:value:int")]
	[InlineData("Value|static|string|pp:items:string[]")]
	[InlineData("Value|static|string|-:value:int;-:value:string")]
	[InlineData("Value|static|string|p:items:int")]
	[InlineData("Value|static|string|p:items:int[,]")]
	[InlineData("Value|static|string|p:items:global::System.Collections.Generic.List<int>")]
	[InlineData("Value|static|string|p:items:string[];-:suffix:string")]
	[InlineData("Value|static|string|p:first:string[];p:second:string[]")]
	[InlineData("Value|static|string|-:value:void")]
	[InlineData("Value|static|global::System.Void|")]
	[InlineData("Value|static|string|-:value:global::System.Void")]
	[InlineData("Value|static|System.Void|")]
	[InlineData("Value|static|global::System.Collections.Generic.IEnumerator<int>|")]
	[InlineData("Value|static|global::System.Collections.Generic.List<int>.Enumerator|")]
	[InlineData("Value|static|global::System.Linq.EnumerableQuery<int>|")]
	[InlineData("Value|static|global::System.Linq.IOrderedEnumerable<int>|")]
	[InlineData("Value|static|global::Missing.ReturnType|")]
	[InlineData("Value|static|global::System.Threading.Tasks.Task<global::Missing.ReturnType>|")]
	[InlineData("Value|static|string|-:value:global::System.Collections.Generic.List<void>")]
	public void ManifestSignature_InvalidEntryRejectsTheWholeProxy(string entry) {
		var entries = ExportDeclaration(0, "Good|static|string|") + "\n" + ExportDeclaration(1, entry);

		AssertRejected(Run(Manifest(count: 2, entries: entries)), "PBG101");
	}

	[Theory]
	[InlineData("global::System.Threading.Tasks.Task")]
	[InlineData("global::System.Threading.Tasks.Task<int>")]
	[InlineData("global::System.Threading.Tasks.ValueTask")]
	[InlineData("global::System.Threading.Tasks.ValueTask<int>")]
	[InlineData("global::System.Collections.Generic.IEnumerable<int>")]
	[InlineData("global::System.Collections.Generic.IAsyncEnumerable<int>")]
	public void ManifestSignature_DeferredReturnWithRefLikeParameterRejectsTheWholeProxy(string returnType) {
		foreach (var parameterType in new[] { "global::System.Span<int>", "global::System.ReadOnlySpan<int>" }) {
			var entries = ExportDeclaration(0, "Good|static|string|") + "\n"
				+ ExportDeclaration(1, $"Value|static|{returnType}|-:items:{parameterType}");

			AssertRejected(Run(Manifest(count: 2, entries: entries)), "PBG101");
		}
	}

	[Theory]
	[InlineData("global::System.Span<int>")]
	[InlineData("global::System.ReadOnlySpan<int>")]
	public void ManifestSignature_SynchronousReturnWithRefLikeParameterCompiles(string parameterType) {
		var entries = ExportDeclaration(0, $"Count|static|int|-:items:{parameterType}") + "\n"
			+ ExportDeclaration(1, $"Accept|static|void|-:items:{parameterType}");
		var outcome = Run(Manifest(count: 2, entries: entries));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		Assert.Contains(outcome.GeneratedSources, generated => generated.HintName == "ModuleBridge.Dep.g.cs");
	}

	[Theory]
	[InlineData("global::System.Threading.Tasks.Task<global::System.Collections.Generic.IEnumerable<int>>")]
	[InlineData("global::System.Threading.Tasks.ValueTask<global::System.Collections.Generic.IAsyncEnumerable<int>>")]
	[InlineData("global::System.Threading.Tasks.Task<global::System.Threading.Tasks.Task<int>>")]
	[InlineData("global::System.Collections.Generic.IEnumerable<global::System.Threading.Tasks.Task<int>>")]
	[InlineData("global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.IEnumerable<int>>")]
	[InlineData("global::System.Collections.Generic.IAsyncEnumerable<global::System.Threading.Tasks.ValueTask<int>>")]
	[InlineData("global::System.Collections.Generic.IEnumerable<int>[]")]
	[InlineData("global::System.Threading.Tasks.Task<int>[]")]
	[InlineData("global::System.Collections.Generic.List<global::System.Threading.Tasks.Task<int>>")]
	[InlineData("global::System.Collections.Generic.List<global::System.Collections.Generic.IEnumerable<int>>")]
	[InlineData("global::System.Collections.Generic.Dictionary<string, global::System.Threading.Tasks.Task<int>[]>")]
	[InlineData("global::System.Collections.Generic.Dictionary<global::System.Threading.Tasks.Task<int>, int>.KeyCollection")]
	[InlineData("global::System.Threading.Tasks.Task<global::System.Collections.Generic.List<global::System.Threading.Tasks.ValueTask<int>?>>")]
	[InlineData("global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.IEnumerator<int>>")]
	[InlineData("global::System.Threading.Tasks.Task<global::System.Linq.IQueryable<int>>")]
	[InlineData("global::System.Collections.Generic.List<global::System.Linq.IOrderedEnumerable<int>>")]
	[InlineData("global::System.Threading.Tasks.ValueTask<global::System.IObservable<int>>")]
	[InlineData("global::System.Collections.Generic.List<global::System.Collections.IEnumerable>")]
	[InlineData("global::System.Threading.Tasks.Task<global::System.Linq.EnumerableQuery<int>>")]
	[InlineData("global::System.Threading.Tasks.Task<global::System.Collections.Generic.List<int>.Enumerator>")]
	[InlineData("global::System.Threading.Tasks.ValueTask<global::System.Collections.Generic.List<int>.Enumerator>")]
	[InlineData("global::System.Collections.Generic.IEnumerable<global::System.Linq.EnumerableQuery<int>>")]
	[InlineData("global::System.Linq.EnumerableQuery<int>[]")]
	[InlineData("global::System.Collections.Generic.List<global::System.Collections.Generic.List<int>.Enumerator?>")]
	public void ManifestSignature_NestedDeferredReturnRejectsTheWholeProxy(string returnType) {
		var entries = ExportDeclaration(0, "Good|static|string|") + "\n" + ExportDeclaration(1, $"Value|static|{returnType}|");

		AssertRejected(Run(Manifest(count: 2, entries: entries)), "PBG101");
	}

	[Theory]
	[InlineData("int")]
	[InlineData("string")]
	[InlineData("int[]")]
	[InlineData("global::System.Collections.Generic.List<int>")]
	[InlineData("global::System.Threading.Tasks.Task")]
	[InlineData("global::System.Threading.Tasks.Task<int>")]
	[InlineData("global::System.Threading.Tasks.Task<global::System.Collections.Generic.List<int>>")]
	[InlineData("global::System.Threading.Tasks.ValueTask")]
	[InlineData("global::System.Threading.Tasks.ValueTask<int>")]
	[InlineData("global::System.Threading.Tasks.ValueTask<int[]>")]
	[InlineData("global::System.Collections.Generic.IEnumerable<int>")]
	[InlineData("global::System.Collections.Generic.IAsyncEnumerable<int>")]
	[InlineData("global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.List<int>>")]
	[InlineData("global::System.Threading.Tasks.Task<global::System.Collections.Generic.Dictionary<int, string>.KeyCollection>")]
	public void ManifestSignature_SupportedReturnShapesCompile(string returnType) {
		var outcome = Run(Manifest(entries: ExportDeclaration(0, $"Value|static|{returnType}|")));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		Assert.Contains(outcome.GeneratedSources, generated => generated.HintName == "ModuleBridge.Dep.g.cs");
	}

	[Fact]
	public void ManifestSignature_MoreThanSixteenParameters_ReportsPbg101() {
		var parameters = string.Join(";", Enumerable.Range(0, 17).Select(index => $"-:p{index}:int"));

		AssertRejected(Run(Manifest(entries: ExportDeclaration(0, $"Wide|static|void|{parameters}"))), "PBG101");
	}

	[Theory]
	[InlineData("Value|static|void|", "public static void Call() => Pengin1011.Core.ModuleBridge.Dep.Value();")]
	[InlineData("Value|instance|void|", "public static void Call() => Pengin1011.Core.ModuleBridge.Dep.Value();")]
	[InlineData("Value|static|void|p:items:string[]", "public static void Call() => Pengin1011.Core.ModuleBridge.Dep.Value(\"first\", \"second\");")]
	[InlineData("Value|static|void|p:items:int[][]", "public static void Call() => Pengin1011.Core.ModuleBridge.Dep.Value(new int[] { 1 }, new int[] { 2 });")]
	public void ManifestSignature_ValidVoidAndParamsExportsCompile(string entry, string call) {
		var source = $"public static class Consumer {{ {call} }}";
		var outcome = GeneratorTestHost.Run(source, "ConsumerModule", (ManifestPath, Manifest(entries: ExportDeclaration(0, entry))));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		Assert.Contains(outcome.GeneratedSources, generated => generated.HintName == "ModuleBridge.Dep.g.cs");
	}

	[Fact]
	public void ManifestSignature_SixteenParameters_Compiles() {
		var parameters = string.Join(";", Enumerable.Range(0, 16).Select(index => $"-:p{index}:int"));
		var outcome = Run(Manifest(entries: ExportDeclaration(0, $"Wide|static|void|{parameters}")));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
	}

	[Theory]
	[InlineData("ModuleBridge")]
	[InlineData("__ModuleBridgeProvider")]
	[InlineData("__ModuleBridgeManifest")]
	public void EmptyExporter_ReservedModuleName_ReportsPbg013(string module) {
		var outcome = GeneratorTestHost.Run("public static class NoExports { }", module);

		Assert.Equal("PBG013", GeneratorTestHost.Single(outcome, "PBG013").Id);
		Assert.DoesNotContain(outcome.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
		GeneratorTestHost.AssertCompiles(outcome);
	}

	[Theory]
	[InlineData("ModuleBridge")]
	[InlineData("__ModuleBridgeProvider")]
	[InlineData("__ModuleBridgeManifest")]
	public void EmptyImportedManifest_ReservedModuleName_ReportsPbg013(string module) {
		AssertRejected(Run(Manifest(module: module, count: 0, entries: "")), "PBG013", module);
	}

	[Fact]
	public void ImportedExport_NamedLikeItsModule_ReportsPbg013() {
		AssertRejected(Run(Manifest(entries: ExportDeclaration(0, "Dep|static|string|"))), "PBG013");
	}

	[Theory]
	[InlineData("")]
	[InlineData("public const string __Module = \"\";")]
	[InlineData("public const string __Module = \"Bad.Module\";")]
	public void ImportedModule_MissingOrInvalid_ReportsPbg102(string module) {
		var manifest = Manifest().Replace("public const string __Module = \"Dep\";", module, StringComparison.Ordinal);

		AssertRejected(Run(manifest), "PBG102");
	}

	[Fact]
	public void UnrelatedAdditionalFile_IsIgnoredEvenWhenItMentionsTheManifest() {
		var outcome = GeneratorTestHost.Run(ConsumerSource, "ConsumerModule", ("Notes.g.cs", "internal static class __ModuleBridgeManifest {"));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		Assert.DoesNotContain(outcome.GeneratedSources, generated => generated.HintName == "ModuleBridge.Dep.g.cs");
	}

	[Fact]
	public void InvalidManifest_DoesNotPreventAValidDependencyFromGenerating() {
		var outcome = GeneratorTestHost.Run(
			ConsumerSource,
			"ConsumerModule",
			(ManifestPath, Manifest(entries: ExportDeclaration(0, "Value|static|string|p:items:int"))),
			("dependencies/GoodDep/ModuleBridgeManifest.g.cs", Manifest(module: "GoodDep")));

		AssertRejected(outcome, "PBG101");
		Assert.Contains(outcome.GeneratedSources, generated => generated.HintName == "ModuleBridge.GoodDep.g.cs");
	}

	[Fact]
	public void DuplicateImportedExport_RejectsTheWholeProxy() {
		var entries = ExportDeclaration(0, "Value|static|string|") + "\n" + ExportDeclaration(1, "Value|static|string|");

		AssertRejected(Run(Manifest(count: 2, entries: entries)), "PBG106");
	}

	[Fact]
	public void DuplicateModule_UsesDistinctManifestDirectories() {
		var manifest = Manifest();
		var outcome = GeneratorTestHost.Run(
			ConsumerSource,
			"ConsumerModule",
			(ManifestPath, manifest),
			("other/Dep/ModuleBridgeManifest.g.cs", manifest));

		Assert.Equal("PBG105", GeneratorTestHost.Single(outcome, "PBG105").Id);
		Assert.DoesNotContain(outcome.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
		Assert.DoesNotContain(outcome.GeneratedSources, generated => generated.HintName == "ModuleBridge.Dep.g.cs");
		GeneratorTestHost.AssertCompiles(outcome);
	}

	private static GeneratorOutcome Run(string text) {
		Assert.True(GeneratorTestHost.HasFrameworkReferences, "BCL references are required for generator tests");
		return GeneratorTestHost.Run(ConsumerSource, "ConsumerModule", (ManifestPath, text));
	}

	private static void AssertRejected(GeneratorOutcome outcome, string diagnosticId, string module = "Dep") {
		Assert.Equal(diagnosticId, GeneratorTestHost.Single(outcome, diagnosticId).Id);
		Assert.DoesNotContain(outcome.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
		Assert.DoesNotContain(outcome.CompilationDiagnostics, diagnostic => diagnostic.Id == "CS8785");
		Assert.DoesNotContain(outcome.GeneratedSources, generated => generated.HintName == $"ModuleBridge.{module}.g.cs");
		GeneratorTestHost.AssertCompiles(outcome);
	}

	private static string Manifest(string module = "Dep", string format = "pengin1011.modulebridge.manifest/3", int count = 1, string? entries = null) {
		return $$"""
namespace Pengin1011.Core {
	internal static class __ModuleBridgeManifest {
		public const string __Format = "{{format}}";
		public const string __Module = "{{module}}";
		public const int __ExportCount = {{count}};
{{entries ?? ExportDeclaration(0, "Value|static|string|")}}
	}
}
""";
	}

	private static string ExportDeclaration(int index, string entry) {
		return $"public const string __Export_{index} = \"{entry}\";";
	}
}
