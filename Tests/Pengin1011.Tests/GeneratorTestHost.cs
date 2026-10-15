using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Pengin1011Generator.AIPreset;
using Pengin1011Generator.ModuleBridge;

namespace Pengin1011.Tests;

internal sealed record GeneratorOutcome(
	ImmutableArray<Diagnostic> GeneratorDiagnostics,
	ImmutableArray<Diagnostic> CompilationDiagnostics,
	ImmutableArray<(string HintName, string Text)> GeneratedSources,
	Compilation OutputCompilation) {
	internal ImmutableArray<Diagnostic> Errors => [.. CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)];

	internal IEnumerable<string> SourceTexts => GeneratedSources.Select(source => source.Text);

	internal string SourceContaining(string fragment) {
		return GeneratedSources.First(source => source.Text.Contains(fragment, StringComparison.Ordinal)).Text;
	}

	internal string SourceByHint(string hintName) {
		return GeneratedSources.First(source => source.HintName == hintName).Text;
	}
}

internal static class GeneratorTestHost {
	private static readonly Lazy<ImmutableArray<MetadataReference>> _references = new(LoadReferences);
	private static readonly Lazy<string> _targetingPackRoot = new(ResolveTargetingPackRoot);

	internal static string FrameworkRoot => _targetingPackRoot.Value;

	internal static bool HasFrameworkReferences => _references.Value.Length > 0;

	internal static ImmutableArray<MetadataReference> ReferenceSet() {
		return _references.Value;
	}

	internal static GeneratorOutcome Run(string source, string assemblyName, MetadataReference extraReference, params (string HintName, string Text)[] additionalTexts) {
		var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
		var tree = CSharpSyntaxTree.ParseText(SourceText.From(source), parseOptions);
		var compilation = CSharpCompilation.Create(
			assemblyName,
			[tree],
			[.. _references.Value, extraReference],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
		return RunCore(compilation, parseOptions, additionalTexts, [new ModuleBridgeGenerator().AsSourceGenerator()]);
	}

	internal static GeneratorOutcome Run(string source, string assemblyName = "TestModule", params (string HintName, string Text)[] additionalTexts) {
		var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
		var tree = CSharpSyntaxTree.ParseText(SourceText.From(source), parseOptions);
		var compilation = CSharpCompilation.Create(
			assemblyName,
			[tree],
			_references.Value,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
		return RunCore(compilation, parseOptions, additionalTexts, [new ModuleBridgeGenerator().AsSourceGenerator()]);
	}

	internal static GeneratorOutcome RunAIPreset(string source, string assemblyName = "TestModule") {
		var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
		var tree = CSharpSyntaxTree.ParseText(SourceText.From(source), parseOptions);
		var compilation = CSharpCompilation.Create(
			assemblyName,
			[tree],
			_references.Value,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
		return RunCore(compilation, parseOptions, [], [new AIPresetUsageGenerator().AsSourceGenerator()]);
	}

	private static GeneratorOutcome RunCore(Compilation compilation, CSharpParseOptions parseOptions, (string HintName, string Text)[] additionalTexts, ISourceGenerator[] generators) {
		var texts = additionalTexts.Select(text => (AdditionalText)new InMemoryAdditionalText(text.HintName, text.Text)).ToImmutableArray();
		var driver = CSharpGeneratorDriver.Create(generators, texts, parseOptions);
		var updated = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
		var runResult = updated.GetRunResult();
		var sources = runResult.Results
			.SelectMany(result => result.GeneratedSources)
			.Select(source => (source.HintName, source.SourceText.ToString()))
			.ToImmutableArray();
		return new GeneratorOutcome(generatorDiagnostics, output.GetDiagnostics(), sources, output);
	}

	internal static string DiagnosticId(GeneratorOutcome outcome, string id) {
		return outcome.GeneratorDiagnostics.FirstOrDefault(d => d.Id == id)?.Id
			?? throw new InvalidOperationException($"expected generator diagnostic {id}, got: {string.Join(", ", outcome.GeneratorDiagnostics.Select(d => d.Id))}");
	}

	internal static Diagnostic Single(GeneratorOutcome outcome, string id) {
		var matches = outcome.GeneratorDiagnostics.Where(d => d.Id == id).ToList();
		if (matches.Count != 1) {
			throw new InvalidOperationException($"expected exactly one {id}, got {matches.Count} (all: {string.Join(", ", outcome.GeneratorDiagnostics.Select(d => d.Id))})");
		}
		return matches[0];
	}

	internal static void AssertNoGeneratorDiagnostics(GeneratorOutcome outcome) {
		Assert.Empty(outcome.GeneratorDiagnostics);
	}

	internal static void AssertCompiles(GeneratorOutcome outcome) {
		Assert.True(outcome.Errors.IsEmpty, string.Join(" | ", outcome.Errors.Select(e => e.Id + " " + e.GetMessage())));
	}

	internal static string ManifestLine(string module, int index) {
		var marker = $"__Export_{index} = \"";
		var start = module.IndexOf(marker, StringComparison.Ordinal);
		Assert.True(start >= 0, $"export {index} missing from manifest: {module}");
		start += marker.Length;
		var end = module.IndexOf('"', start);
		return module.Substring(start, end - start);
	}

	private static ImmutableArray<MetadataReference> LoadReferences() {
		var builder = ImmutableArray.CreateBuilder<MetadataReference>();
		if (Directory.Exists(_targetingPackRoot.Value)) {
			var refDirectory = Path.Combine(_targetingPackRoot.Value, "Microsoft.NETCore.App.Ref");
			var version = Directory.Exists(refDirectory)
				? Directory.GetDirectories(refDirectory).OrderByDescending(ParseVersion).FirstOrDefault()
				: null;
			var framework = version == null
				? null
				: Directory.GetDirectories(version, "ref", SearchOption.TopDirectoryOnly).FirstOrDefault();
			var referenceDirectory = framework == null ? null : Directory.GetDirectories(framework).OrderBy(name => name, StringComparer.Ordinal).FirstOrDefault();
			if (referenceDirectory != null) {
				foreach (var file in Directory.GetFiles(referenceDirectory, "*.dll")) {
					builder.Add(MetadataReference.CreateFromFile(file));
				}
			}
		}
		var host = Path.Combine(AppContext.BaseDirectory, "Pengin1011.dll");
		if (File.Exists(host)) builder.Add(MetadataReference.CreateFromFile(host));
		return builder.ToImmutable();
	}

	private static Version ParseVersion(string directory) {
		var name = Path.GetFileName(directory);
		var dash = name.IndexOf('-');
		if (dash >= 0) name = name[..dash];
		return Version.TryParse(name, out var parsed) ? parsed : new Version(0, 0);
	}

	private static string ResolveTargetingPackRoot() {
		var directory = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		for (var current = directory; current != null; current = current.Parent) {
			var candidate = Path.Combine(current.FullName, "packs");
			if (Directory.Exists(Path.Combine(candidate, "Microsoft.NETCore.App.Ref"))) return candidate;
		}
		return string.Empty;
	}

	private sealed class InMemoryAdditionalText : AdditionalText {
		private readonly SourceText _text;

		internal InMemoryAdditionalText(string path, string text) {
			Path = path;
			_text = SourceText.From(text);
		}

		public override string Path { get; }

		public override SourceText GetText(CancellationToken cancellationToken = default) {
			return _text;
		}
	}
}
