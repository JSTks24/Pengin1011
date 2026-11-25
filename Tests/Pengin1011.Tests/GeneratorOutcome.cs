using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

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
