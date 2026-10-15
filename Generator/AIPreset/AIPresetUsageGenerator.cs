using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Pengin1011Generator.AIPreset;

[Generator]
public sealed class AIPresetUsageGenerator : IIncrementalGenerator {
	private const string ClientMetadataName = "Pengin1011.Services.AI.AIClient";
	private const string ProviderTypeName = "__AIPresetUsageProvider";
	private static readonly string[] _entryNames = ["CompleteAsync", "StreamAsync"];

	public void Initialize(IncrementalGeneratorInitializationContext context) {
		var calls = context.SyntaxProvider.CreateSyntaxProvider(
			static (node, _) => IsCandidate(node),
			static (ctx, ct) => Analyze(ctx, ct)
		).Where(static info => info != null).Select(static (info, _) => info!);
		context.RegisterSourceOutput(calls.Collect(), static (spc, results) => Execute(spc, results));
	}

	private sealed record UsageInfo(string CallSite, bool IsDefault, string PresetName);

	private sealed record InvalidInfo(Location Location, string Text);

	private sealed record BypassInfo(Location Location, string Text);

	private sealed record AnalysisResult(UsageInfo? Usage, InvalidInfo? Invalid, BypassInfo? Bypass);

	private static bool IsCandidate(SyntaxNode node) {
		if (node is InvocationExpressionSyntax invocation) {
			return Array.IndexOf(_entryNames, NameOf(invocation.Expression)) >= 0;
		}
		return node is IdentifierNameSyntax identifier && Array.IndexOf(_entryNames, identifier.Identifier.ValueText) >= 0;
	}

	private static string NameOf(ExpressionSyntax expression) {
		return expression switch {
			MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
			MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
			IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
			GenericNameSyntax generic => generic.Identifier.ValueText,
			_ => "",
		};
	}

	private static AnalysisResult? Analyze(GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct) {
		if (ctx.Node is InvocationExpressionSyntax invocation) return AnalyzeInvocation(ctx, invocation, ct);
		var expression = ctx.Node.Parent is MemberAccessExpressionSyntax member && member.Name == ctx.Node ? member : ctx.Node;
		if (expression.Parent is InvocationExpressionSyntax directCall && directCall.Expression == expression) return null;
		if (expression.Ancestors().OfType<InvocationExpressionSyntax>().Any(call => ctx.SemanticModel.GetOperation(call, ct)?.Kind == OperationKind.NameOf)) return null;
		var symbols = ctx.SemanticModel.GetSymbolInfo(expression, ct);
		var method = symbols.Symbol as IMethodSymbol ?? symbols.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
		if (method == null) return null;
		if (method.ContainingType == null || method.ContainingType.ToDisplayString() != ClientMetadataName) return null;
		if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation)) return null;
		if (Array.IndexOf(_entryNames, method.Name) < 0) return null;
		return new AnalysisResult(null, null, new BypassInfo(ctx.Node.GetLocation(), method.Name));
	}

	private static AnalysisResult? AnalyzeInvocation(GeneratorSyntaxContext ctx, InvocationExpressionSyntax invocation, System.Threading.CancellationToken ct) {
		var symbol = ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol;
		if (symbol is not IMethodSymbol method) return null;
		if (method.ContainingType == null || method.ContainingType.ToDisplayString() != ClientMetadataName) return null;
		if (IsInsideClient(ctx, invocation, ct)) return null;
		if (method.Parameters.Length == 0) return null;
		var callSite = CallSiteText(ctx, invocation, method, ct);
		if (method.Parameters[0].Type.SpecialType != SpecialType.System_String) {
			return new AnalysisResult(new UsageInfo(callSite, true, ""), null, null);
		}
		var argument = FindArgument(invocation, method.Parameters[0].Name);
		if (argument == null) return new AnalysisResult(new UsageInfo(callSite, true, ""), null, null);
		var constant = ctx.SemanticModel.GetConstantValue(argument.Expression, ct);
		if (!constant.HasValue) {
			return new AnalysisResult(null, new InvalidInfo(argument.Expression.GetLocation(), argument.Expression.ToString()), null);
		}
		if (constant.Value is not string presetName || presetName.Length == 0) {
			return new AnalysisResult(new UsageInfo(callSite, true, ""), null, null);
		}
		return new AnalysisResult(new UsageInfo(callSite, false, presetName), null, null);
	}

	private static bool IsInsideClient(GeneratorSyntaxContext ctx, InvocationExpressionSyntax invocation, System.Threading.CancellationToken ct) {
		var caller = ctx.SemanticModel.GetEnclosingSymbol(invocation.SpanStart, ct) as IMethodSymbol;
		var container = caller?.ContainingType;
		return container != null && container.ToDisplayString() == ClientMetadataName;
	}

	private static string CallSiteText(GeneratorSyntaxContext ctx, InvocationExpressionSyntax invocation, IMethodSymbol method, System.Threading.CancellationToken ct) {
		var caller = ctx.SemanticModel.GetEnclosingSymbol(invocation.SpanStart, ct) as IMethodSymbol;
		var owner = caller?.ContainingType?.ToDisplayString() ?? "?";
		var name = caller?.Name ?? method.Name;
		return $"{owner}#{name}";
	}

	private static ArgumentSyntax? FindArgument(InvocationExpressionSyntax invocation, string parameterName) {
		var arguments = invocation.ArgumentList.Arguments;
		foreach (var argument in arguments) {
			if (argument.NameColon != null && argument.NameColon.Name.Identifier.ValueText == parameterName) return argument;
		}
		if (arguments.Count == 0) return null;
		var first = arguments[0];
		return first.NameColon == null ? first : null;
	}

	private static void Execute(SourceProductionContext spc, ImmutableArray<AnalysisResult> results) {
		foreach (var result in results) {
			if (result.Invalid != null) {
				spc.ReportDiagnostic(Diagnostic.Create(AIPresetDiagnostics.RulePresetNameNotConstant, result.Invalid.Location, result.Invalid.Text));
			}
			if (result.Bypass != null) {
				spc.ReportDiagnostic(Diagnostic.Create(AIPresetDiagnostics.RulePresetNameBypassed, result.Bypass.Location, result.Bypass.Text));
			}
		}
		var usages = results
			.Where(static result => result.Usage != null)
			.Select(static result => result.Usage!)
			.Distinct()
			.OrderBy(static usage => usage.CallSite, StringComparer.Ordinal)
			.ThenBy(static usage => usage.PresetName, StringComparer.Ordinal)
			.ToImmutableArray();
		if (usages.IsEmpty) return;
		Emit(spc, usages);
	}

	private static void Emit(SourceProductionContext spc, ImmutableArray<UsageInfo> usages) {
		var sb = new StringBuilder();
		sb.Append("// <auto-generated/>\n#nullable enable\n");
		sb.Append("[assembly: global::Pengin1011.Services.AI.AIPresetUsageProvider(typeof(global::Pengin1011.Services.AI.").Append(ProviderTypeName).Append("))]\n");
		sb.Append("namespace Pengin1011.Services.AI;\n\n");
		sb.Append("internal sealed class ").Append(ProviderTypeName).Append(" : global::Pengin1011.Services.AI.IAIPresetUsageProvider {\n");
		sb.Append("\tpublic void Register(global::Pengin1011.Services.AI.AIPresetUsageRegistrar registrar) {\n");
		foreach (var usage in usages) {
			var callSite = Literal(usage.CallSite);
			if (usage.IsDefault) {
				sb.Append("\t\tregistrar.AddDefault(").Append(callSite).Append(");\n");
			} else {
				sb.Append("\t\tregistrar.AddNamed(").Append(callSite).Append(", ").Append(Literal(usage.PresetName)).Append(");\n");
			}
		}
		sb.Append("\t}\n}\n");
		spc.AddSource("AIPresetUsage.g.cs", SourceText.From(sb.ToString(), new UTF8Encoding(false)));
	}

	private static string Literal(string value) {
		return SymbolDisplay.FormatLiteral(value, quote: true);
	}
}
