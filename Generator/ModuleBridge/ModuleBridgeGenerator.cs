using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Pengin1011Generator.ModuleBridge;

[Generator]
public sealed class ModuleBridgeGenerator : IIncrementalGenerator {
	private const string ManifestFormat = "pengin1011.modulebridge.manifest/3";
	private const int HostContractVersion = 4;
	private const string ExportAttributeMetadataName = "Pengin1011.Core.ModuleBridgeExportAttribute";
	private const string RuntimeInterfaceMetadataName = "Pengin1011.Core.Modules.IModuleRuntime";
	private const string ContainerName = "ModuleBridge";
	private const string ProviderTypeName = "__ModuleBridgeProvider";
	private const string ManifestTypeName = "__ModuleBridgeManifest";

	public void Initialize(IncrementalGeneratorInitializationContext context) {
		var exports = context.SyntaxProvider.CreateSyntaxProvider(
			static (node, _) => node is MethodDeclarationSyntax { AttributeLists.Count: > 0 },
			static (ctx, _) => {
				if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) is not IMethodSymbol method) return null;
				return IsExportMethod(method, ctx.SemanticModel.Compilation) ? method : null;
			}
		).Where(static m => m != null).Select(static (m, _) => m!);
		var runtimes = context.SyntaxProvider.CreateSyntaxProvider(
			static (node, _) => node is TypeDeclarationSyntax,
			static (ctx, _) => {
				if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) is not INamedTypeSymbol type) return null;
				if (type.TypeKind != TypeKind.Class || type.IsAbstract) return null;
				var iface = ctx.SemanticModel.Compilation.GetTypeByMetadataName(RuntimeInterfaceMetadataName);
				if (iface == null) return null;
				return type.AllInterfaces.Contains(iface, SymbolEqualityComparer.Default) ? type : null;
			}
		).Where(static t => t != null).Select(static (t, _) => t!);
		var moduleName = context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? string.Empty);
		context.RegisterSourceOutput(
			exports.Collect().Combine(runtimes.Collect()).Combine(context.CompilationProvider).Combine(moduleName),
			static (spc, pair) => ExecuteExports(spc, pair.Left.Left.Left, pair.Left.Left.Right, pair.Left.Right, pair.Right));
		var manifests = context.AdditionalTextsProvider
			.Where(static text => string.Equals(Path.GetFileName(text.Path), "ModuleBridgeManifest.g.cs", StringComparison.Ordinal))
			.Select(static (text, ct) => (Path: text.Path, Text: text.GetText(ct)?.ToString() ?? string.Empty))
			.Collect();
		context.RegisterSourceOutput(manifests.Combine(context.CompilationProvider), static (spc, pair) => ExecuteManifests(spc, pair.Left, pair.Right));
	}

	private sealed record ExportInfo(string Name, bool IsInstance, string ContainingTypeFqn, string ReturnText, string ReturnCSharp, ImmutableArray<ParameterInfo> Parameters);

	private sealed record ParameterInfo(string Name, string TypeText, string TypeCSharp, bool IsParams);

	private sealed record ManifestExport(string Name, string ReturnText, string ReturnCSharp, ImmutableArray<ParameterInfo> Parameters);

	private static bool IsExportMethod(IMethodSymbol method, Compilation compilation) {
		var attribute = compilation.GetTypeByMetadataName(ExportAttributeMetadataName);
		if (attribute == null) return false;
		foreach (var candidate in method.GetAttributes()) {
			if (SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute)) return true;
		}
		return false;
	}

	private static void ExecuteExports(SourceProductionContext spc, ImmutableArray<IMethodSymbol> exports, ImmutableArray<INamedTypeSymbol> runtimes, Compilation compilation, string moduleName) {
		if (IsReservedModuleName(moduleName)) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleNameCollision, null, moduleName));
			return;
		}
		var runtimeTypes = runtimes.Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>().ToImmutableArray();
		var scope = new BridgeTypeScope(compilation, moduleName);
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var valid = new List<ExportInfo>();
		var ordered = exports.OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.ContainingType.ToDisplayString(), StringComparer.Ordinal);
		foreach (var method in ordered) {
			var info = Validate(spc, method, runtimeTypes, scope);
			if (info == null) continue;
			if (!seen.Add(info.Name)) {
				spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleDuplicateExport, method.Locations.FirstOrDefault(), info.Name));
				continue;
			}
			valid.Add(info);
		}
		EmitManifest(spc, moduleName, valid);
		if (valid.Count > 0) EmitProvider(spc, valid, runtimeTypes);
	}

	private static ExportInfo? Validate(SourceProductionContext spc, IMethodSymbol method, ImmutableArray<INamedTypeSymbol> runtimeTypes, BridgeTypeScope scope) {
		var location = method.Locations.FirstOrDefault();
		if (method.DeclaredAccessibility != Accessibility.Public) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleMethodNotPublic, location));
			return null;
		}
		var type = method.ContainingType;
		for (var current = type; current != null; current = current.ContainingType) {
			if (current.DeclaredAccessibility != Accessibility.Public) {
				spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleTypeNotPublic, location));
				return null;
			}
		}
		if (type.IsGenericType) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleGenericDeclaringType, location));
			return null;
		}
		if (method.IsGenericMethod) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleGenericMethod, location));
			return null;
		}
		if (method.RefKind != RefKind.None) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleByRefSignature, location));
			return null;
		}
		foreach (var parameter in method.Parameters) {
			if (parameter.RefKind != RefKind.None) {
				spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleByRefSignature, location));
				return null;
			}
			if (parameter.IsOptional) {
				spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleOptionalParameter, location));
				return null;
			}
			if (parameter.IsParams && parameter.Type is not IArrayTypeSymbol { Rank: 1 }) {
				spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleUnsupportedType, location, parameter.Type.ToDisplayString()));
				return null;
			}
		}
		if (method.Parameters.Length > 16) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleTooManyParameters, location, method.Parameters.Length));
			return null;
		}
		var isInstance = !method.IsStatic;
		if (isInstance && (runtimeTypes.Length != 1 || !SymbolEqualityComparer.Default.Equals(type, runtimeTypes[0]))) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleInstanceNotOnRuntime, location));
			return null;
		}
		if (IsCollidingName(method.Name, scope.ModuleAssemblyName)) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleNameCollision, location, method.Name));
			return null;
		}
		string returnText;
		string returnCSharp;
		if (method.ReturnsVoid) {
			returnText = BridgeTypeText.VoidText;
			returnCSharp = BridgeTypeText.VoidText;
		} else {
			if (IsUnsupportedDeferredReturn(method.ReturnType)) {
				spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleUnsupportedType, location, method.ReturnType.ToDisplayString()));
				return null;
			}
			var serialized = BridgeTypeText.TrySerialize(method.ReturnType, scope, out var returnFailure);
			if (serialized == null) {
				ReportTypeFailure(spc, location, returnFailure, method.ReturnType);
				return null;
			}
			var parsed = BridgeTypeText.TryToCSharp(serialized);
			if (parsed == null) {
				spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleUnsupportedType, location, method.ReturnType.ToDisplayString()));
				return null;
			}
			returnText = serialized;
			returnCSharp = parsed;
		}
		var parameters = ImmutableArray.CreateBuilder<ParameterInfo>(method.Parameters.Length);
		foreach (var parameter in method.Parameters) {
			var text = BridgeTypeText.TrySerialize(parameter.Type, scope, out var failure);
			if (text == null) {
				ReportTypeFailure(spc, location, failure, parameter.Type);
				return null;
			}
			var csharp = BridgeTypeText.TryToCSharp(text);
			if (csharp == null) {
				spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleUnsupportedType, location, parameter.Type.ToDisplayString()));
				return null;
			}
			parameters.Add(new ParameterInfo(parameter.Name, text, csharp, parameter.IsParams));
		}
		return new ExportInfo(method.Name, isInstance, BridgeTypeText.QualifiedName(type), returnText, returnCSharp, parameters.MoveToImmutable());
	}

	private static bool IsUnsupportedDeferredReturn(ITypeSymbol type) {
		if (type.TypeKind == TypeKind.Error) return true;
		if (ReturnShape.IsUnsupported(type.ToDisplayString(BridgeTypeText.TypeFormat))) return true;
		if (type is IArrayTypeSymbol array) return IsUnsupportedDeferredReturn(array.ElementType);
		if (type is not INamedTypeSymbol named) return false;
		return named.AllInterfaces.Any(iface => iface.MetadataName != "IEnumerable" && ReturnShape.IsUnsupportedInterface(iface.ToDisplayString(BridgeTypeText.TypeFormat)))
			|| named.TypeArguments.Any(IsUnsupportedDeferredReturn)
			|| named.ContainingType != null && IsUnsupportedDeferredReturn(named.ContainingType);
	}

	private static bool IsCollidingName(string exportName, string moduleName) {
		return string.Equals(exportName, moduleName, StringComparison.Ordinal) || IsReservedModuleName(moduleName);
	}

	private static bool IsReservedModuleName(string moduleName) {
		if (string.Equals(moduleName, ContainerName, StringComparison.Ordinal)) return true;
		return string.Equals(moduleName, ProviderTypeName, StringComparison.Ordinal) || string.Equals(moduleName, ManifestTypeName, StringComparison.Ordinal);
	}

	private static void ReportTypeFailure(SourceProductionContext spc, Location? location, BridgeTypeText.SerializeFailure failure, ITypeSymbol type) {
		var rule = failure == BridgeTypeText.SerializeFailure.LocalType ? BridgeDiagnostics.RuleLocalType : BridgeDiagnostics.RuleUnsupportedType;
		spc.ReportDiagnostic(Diagnostic.Create(rule, location, type.ToDisplayString()));
	}

	private static void EmitManifest(SourceProductionContext spc, string moduleName, List<ExportInfo> valid) {
		if (valid.Count > 0 && !BridgeTypeText.IsValidModuleName(moduleName)) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleInvalidModuleName, null, moduleName));
		}
		var sb = new StringBuilder();
		sb.Append("// <auto-generated/>\n#nullable enable\n");
		sb.Append("[assembly: global::Pengin1011.Core.ModuleHostContract(").Append(HostContractVersion).Append(")]\n");
		sb.Append("namespace Pengin1011.Core;\n\n");
		sb.Append("internal static class ").Append(ManifestTypeName).Append(" {\n");
		sb.Append("\tpublic const string __Format = \"").Append(ManifestFormat).Append("\";\n");
		sb.Append("\tpublic const string __Module = \"").Append(EscapeLiteral(moduleName)).Append("\";\n");
		sb.Append("\tpublic const int __ExportCount = ").Append(valid.Count).Append(";\n");
		for (var i = 0; i < valid.Count; i++) {
			sb.Append("\tpublic const string __Export_").Append(i).Append(" = \"").Append(EscapeLiteral(ManifestLine(valid[i]))).Append("\";\n");
		}
		sb.Append("}\n");
		spc.AddSource("ModuleBridgeManifest.g.cs", SourceText.From(sb.ToString(), new UTF8Encoding(false)));
	}

	private static void EmitProvider(SourceProductionContext spc, List<ExportInfo> valid, ImmutableArray<INamedTypeSymbol> runtimeTypes) {
		var sb = new StringBuilder();
		sb.Append("// <auto-generated/>\n#nullable enable\n");
		sb.Append("[assembly: global::Pengin1011.Core.ModuleBridgeProvider(typeof(global::Pengin1011.Core.").Append(ProviderTypeName).Append("))]\n");
		sb.Append("namespace Pengin1011.Core;\n\n");
		sb.Append("internal sealed class ").Append(ProviderTypeName).Append(" : global::Pengin1011.Core.IModuleBridgeProvider {\n");
		sb.Append("\tpublic void Register(global::Pengin1011.Core.ModuleBridgeRegistrar registrar, global::Pengin1011.Core.Modules.IModuleRuntime runtime) {\n");
		if (valid.Any(e => e.IsInstance)) {
			sb.Append("\t\tvar rt = (").Append(BridgeTypeText.QualifiedName(runtimeTypes[0])).Append(")runtime;\n");
		}
		foreach (var e in valid) {
			var method = BridgeTypeText.EscapeIdentifier(e.Name);
			var target = e.IsInstance ? "rt." + method : e.ContainingTypeFqn + "." + method;
			sb.Append("\t\tregistrar.Add(\"").Append(EscapeLiteral(e.Name)).Append("\", (").Append(DelegateType(e)).Append(")").Append(target).Append(");\n");
		}
		sb.Append("\t}\n}\n");
		spc.AddSource("ModuleBridgeProvider.g.cs", SourceText.From(sb.ToString(), new UTF8Encoding(false)));
	}

	private static void ExecuteManifests(SourceProductionContext spc, ImmutableArray<(string Path, string Text)> inputs, Compilation compilation) {
		if (inputs.IsEmpty) return;
		var bindingTree = CSharpSyntaxTree.ParseText("namespace Pengin1011.Core;", (CSharpParseOptions?)compilation.SyntaxTrees.FirstOrDefault()?.Options, cancellationToken: spc.CancellationToken);
		var model = compilation.AddSyntaxTrees(bindingTree).GetSemanticModel(bindingTree);
		var manifests = new List<(string Module, ImmutableArray<string> Lines)>();
		foreach (var input in inputs) {
			if (TryReadManifest(spc, input.Path, input.Text, out var module, out var lines)) manifests.Add((module, lines));
		}
		foreach (var group in manifests.GroupBy(manifest => manifest.Module, StringComparer.Ordinal)) {
			var module = group.Key;
			if (group.Count() > 1) {
				spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestDuplicateModule, null, module));
				continue;
			}
			var exports = new List<ManifestExport>();
			var names = new HashSet<string>(StringComparer.Ordinal);
			var valid = true;
			foreach (var line in group.First().Lines) {
				var parsed = ParseManifestExport(spc, module, line, model);
				if (parsed == null) {
					valid = false;
					continue;
				}
				if (!names.Add(parsed.Name)) {
					spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestDuplicateExport, null, module, parsed.Name));
					valid = false;
					continue;
				}
				exports.Add(parsed);
			}
			if (!valid) continue;
			exports.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
			EmitProxy(spc, module, exports);
		}
	}

	private static bool TryReadManifest(SourceProductionContext spc, string path, string text, out string module, out ImmutableArray<string> lines) {
		module = string.Empty;
		lines = ImmutableArray<string>.Empty;
		var root = CSharpSyntaxTree.ParseText(text, cancellationToken: spc.CancellationToken).GetRoot(spc.CancellationToken);
		var declarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(type => type.Identifier.ValueText == ManifestTypeName).ToArray();
		if (root.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) || declarations.Length != 1) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestParse, null, path));
			return false;
		}
		var constants = new Dictionary<string, object>(StringComparer.Ordinal);
		var exportEntries = 0;
		var duplicateIndex = false;
		foreach (var member in declarations[0].Members) {
			if (member is not FieldDeclarationSyntax field || !field.Modifiers.Any(SyntaxKind.ConstKeyword) || field.Declaration.Variables.Count != 1) {
				spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestParse, null, path));
				return false;
			}
			var variable = field.Declaration.Variables[0];
			var name = variable.Identifier.ValueText;
			var exportField = name.StartsWith("__Export_", StringComparison.Ordinal);
			if (exportField) exportEntries++;
			if (constants.ContainsKey(name)) {
				if (exportField) {
					duplicateIndex = true;
					continue;
				}
				spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestParse, null, name));
				return false;
			}
			var countField = name == "__ExportCount";
			if (field.Declaration.Type is not PredefinedTypeSyntax type || !type.Keyword.IsKind(countField ? SyntaxKind.IntKeyword : SyntaxKind.StringKeyword)
				|| variable.Initializer?.Value is not LiteralExpressionSyntax literal
				|| (countField ? literal.Token.Value is not int : literal.Token.Value is not string)) {
				spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestParse, null, name));
				return false;
			}
			constants.Add(name, literal.Token.Value!);
		}
		if (!constants.TryGetValue("__Module", out var moduleValue) || moduleValue is not string moduleName || !BridgeTypeText.IsValidModuleName(moduleName)) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestModuleInvalid, null, moduleValue ?? string.Empty));
			return false;
		}
		module = moduleName;
		if (IsReservedModuleName(module)) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleNameCollision, null, module));
			return false;
		}
		if (!constants.TryGetValue("__Format", out var formatValue)) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestParse, null, "__Format"));
			return false;
		}
		if (!string.Equals((string)formatValue, ManifestFormat, StringComparison.Ordinal)) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestFormatOutdated, null, module));
			return false;
		}
		if (!constants.TryGetValue("__ExportCount", out var countValue) || countValue is not int declared || declared < 0) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestParse, null, "__ExportCount"));
			return false;
		}
		var indexed = new SortedDictionary<int, string>();
		foreach (var constant in constants) {
			if (constant.Key is "__Format" or "__Module" or "__ExportCount") continue;
			if (!constant.Key.StartsWith("__Export_", StringComparison.Ordinal)) {
				spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestParse, null, constant.Key));
				return false;
			}
			var suffix = constant.Key.Substring("__Export_".Length);
			if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index >= declared
				|| suffix != index.ToString(CultureInfo.InvariantCulture) || indexed.ContainsKey(index)) {
				spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestCountMismatch, null, declared, exportEntries));
				return false;
			}
			indexed.Add(index, (string)constant.Value);
		}
		if (duplicateIndex || indexed.Count != declared) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestCountMismatch, null, declared, exportEntries));
			return false;
		}
		lines = indexed.Values.ToImmutableArray();
		return true;
	}

	private static ManifestExport? ParseManifestExport(SourceProductionContext spc, string module, string line, SemanticModel model) {
		var parts = line.Split('|');
		if (parts.Length != 4 || !BridgeTypeText.IsValidModuleName(parts[0]) || parts[1] is not ("static" or "instance")) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestParse, null, line));
			return null;
		}
		if (IsCollidingName(parts[0], module)) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleNameCollision, null, parts[0]));
			return null;
		}
		var returnCSharp = ParseManifestType(parts[2], true);
		var returnSymbol = returnCSharp == null || returnCSharp == BridgeTypeText.VoidText ? null
			: model.GetSpeculativeTypeInfo(model.SyntaxTree.Length, SyntaxFactory.ParseTypeName(returnCSharp), SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
		if (returnCSharp == null || ReturnShape.IsUnsupported(parts[2])
			|| returnCSharp != BridgeTypeText.VoidText && (returnSymbol == null || IsUnsupportedDeferredReturn(returnSymbol))) {
			spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestParse, null, line));
			return null;
		}
		var parameters = ImmutableArray.CreateBuilder<ParameterInfo>();
		var names = new HashSet<string>(StringComparer.Ordinal);
		if (parts[3].Length > 0) {
			var rawParameters = parts[3].Split(';');
			if (rawParameters.Length > 16) {
				spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestParse, null, line));
				return null;
			}
			for (var i = 0; i < rawParameters.Length; i++) {
				var raw = rawParameters[i];
				var flags = raw.IndexOf(':');
				var name = raw.IndexOf(':', flags + 1);
				if (flags != 1 || name <= flags + 1 || raw[0] is not ('-' or 'p')) {
					spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestParse, null, line));
					return null;
				}
				var parameterName = raw.Substring(flags + 1, name - flags - 1);
				var typeText = raw.Substring(name + 1);
				var typeCSharp = ParseManifestType(typeText, false);
				var isParams = raw[0] == 'p';
				var parameterType = typeCSharp == null ? null : SyntaxFactory.ParseTypeName(typeCSharp);
				if (parameterType is NullableTypeSyntax nullable) parameterType = nullable.ElementType;
				if (typeCSharp == null || !SyntaxFacts.IsValidIdentifier(parameterName) || !names.Add(parameterName)
					|| isParams && (i != rawParameters.Length - 1 || parameterType is not ArrayTypeSyntax)) {
					spc.ReportDiagnostic(Diagnostic.Create(BridgeDiagnostics.RuleManifestParse, null, line));
					return null;
				}
				parameters.Add(new ParameterInfo(parameterName, typeText, typeCSharp, isParams));
			}
		}
		return new ManifestExport(parts[0], parts[2], returnCSharp, parameters.ToImmutable());
	}

	private static string? ParseManifestType(string text, bool allowVoid) {
		if (allowVoid && text == BridgeTypeText.VoidText) return text;
		var type = SyntaxFactory.ParseTypeName(text, 0, true);
		return !type.ContainsDiagnostics && type.FullSpan.Length == text.Length && IsManifestType(type) ? text : null;
	}

	private static bool IsManifestType(TypeSyntax type) {
		return type switch {
			PredefinedTypeSyntax predefined => !predefined.Keyword.IsKind(SyntaxKind.VoidKeyword),
			IdentifierNameSyntax => true,
			GenericNameSyntax generic => generic.TypeArgumentList.Arguments.All(IsManifestType),
			QualifiedNameSyntax qualified => !IsVoidName(qualified) && IsManifestType(qualified.Left) && IsManifestType(qualified.Right),
			AliasQualifiedNameSyntax alias => alias.Alias.Identifier.ValueText == "global" && IsManifestType(alias.Name),
			NullableTypeSyntax nullable => IsManifestType(nullable.ElementType),
			ArrayTypeSyntax array => array.RankSpecifiers.All(rank => rank.Rank == 1) && IsManifestType(array.ElementType),
			_ => false,
		};
	}

	private static bool IsVoidName(QualifiedNameSyntax type) {
		if (type.Right.Identifier.ValueText != "Void") return false;
		return type.Left is IdentifierNameSyntax { Identifier.ValueText: "System" }
			or AliasQualifiedNameSyntax { Alias.Identifier.ValueText: "global", Name.Identifier.ValueText: "System" };
	}

	private static void EmitProxy(SourceProductionContext spc, string module, List<ManifestExport> exports) {
		var sb = new StringBuilder();
		sb.Append("// <auto-generated/>\n#nullable enable\nnamespace Pengin1011.Core;\n\n");
		sb.Append("internal static partial class ").Append(ContainerName).Append(" {\n");
		sb.Append("\tpublic static class ").Append(BridgeTypeText.EscapeIdentifier(module)).Append(" {\n");
		foreach (var e in exports) {
			var isVoid = e.ReturnText == BridgeTypeText.VoidText;
			var returnType = isVoid ? "void" : e.ReturnCSharp;
			var signature = string.Join(", ", e.Parameters.Select(p => $"{(p.IsParams ? "params " : "")}{p.TypeCSharp} {BridgeTypeText.EscapeIdentifier(p.Name)}"));
			var arguments = string.Join(", ", e.Parameters.Select(p => BridgeTypeText.EscapeIdentifier(p.Name)));
			var parameterTypes = string.Join(", ", e.Parameters.Select(p => p.TypeCSharp));
			string delegateType;
			if (e.Parameters.Length == 0) {
				delegateType = isVoid ? "global::System.Action" : $"global::System.Func<{e.ReturnCSharp}>";
			} else {
				delegateType = isVoid ? $"global::System.Action<{parameterTypes}>" : $"global::System.Func<{parameterTypes}, {e.ReturnCSharp}>";
			}
			var name = BridgeTypeText.EscapeIdentifier(e.Name);
			var callName = "call";
			while (e.Parameters.Any(parameter => parameter.Name == callName)) callName += "_";
			var handlerName = "handler";
			while (e.Parameters.Any(parameter => parameter.Name == handlerName)) handlerName += "_";
			var admission = $"global::Pengin1011.Core.ModuleBridgeDispatcher.Admit<{delegateType}>(\"" + EscapeLiteral(module) + "\", \"" + EscapeLiteral(e.Name) + "\")";
			sb.Append("\t\tpublic static ").Append(returnType).Append(' ').Append(name).Append('(').Append(signature).Append(") {\n");
			var shape = ReturnShape.Of(e.ReturnText);
			if (shape is ReturnKind.AsyncEnumerable or ReturnKind.SyncEnumerable) {
				var scope = shape == ReturnKind.AsyncEnumerable ? "RunAsyncEnumerable" : "RunSyncEnumerable";
				var itemType = ((GenericNameSyntax)((QualifiedNameSyntax)ReturnShape.TypeOf(e.ReturnText)).Right).TypeArgumentList.Arguments[0].ToString();
				sb.Append("\t\t\treturn global::Pengin1011.Core.ModuleBridgeLeaseScope.").Append(scope).Append('<').Append(delegateType).Append(", ").Append(itemType).Append(">(() => ").Append(admission).Append(", ").Append(handlerName).Append(" => ").Append(handlerName).Append('(').Append(arguments).Append("));\n");
			} else {
				sb.Append("\t\t\tvar ").Append(callName).Append(" = ").Append(admission).Append(";\n");
				var scope = shape switch {
					ReturnKind.Task => "RunTask",
					ReturnKind.TaskOfT => "RunTaskOf",
					ReturnKind.ValueTask => "RunValueTask",
					ReturnKind.ValueTaskOfT => "RunValueTaskOf",
					_ => null,
				};
				if (scope != null) {
					sb.Append("\t\t\treturn global::Pengin1011.Core.ModuleBridgeLeaseScope.").Append(scope).Append('(').Append(callName).Append(", () => ").Append(callName).Append(".Handler(").Append(arguments).Append("));\n");
				} else {
					sb.Append("\t\t\tusing (").Append(callName).Append(".Lease) {\n\t\t\t\t").Append(isVoid ? "" : "return ").Append(callName).Append(".Handler(").Append(arguments).Append(");\n\t\t\t}\n");
				}
			}
			sb.Append("\t\t}\n");
		}
		sb.Append("\t}\n}\n");
		spc.AddSource($"ModuleBridge.{module}.g.cs", SourceText.From(sb.ToString(), new UTF8Encoding(false)));
	}

	private enum ReturnKind {
		Value,
		Void,
		Task,
		TaskOfT,
		ValueTask,
		ValueTaskOfT,
		AsyncEnumerable,
		SyncEnumerable,
	}

	private static class ReturnShape {
		internal static ReturnKind Of(string returnText) {
			if (returnText == BridgeTypeText.VoidText) return ReturnKind.Void;
			return Of(TypeOf(returnText));
		}

		private static ReturnKind Of(TypeSyntax type) {
			return NameOf(type) switch {
				"System.Threading.Tasks.Task" => ReturnKind.Task,
				"System.Threading.Tasks.Task`1" => ReturnKind.TaskOfT,
				"System.Threading.Tasks.ValueTask" => ReturnKind.ValueTask,
				"System.Threading.Tasks.ValueTask`1" => ReturnKind.ValueTaskOfT,
				"System.Collections.Generic.IAsyncEnumerable`1" => ReturnKind.AsyncEnumerable,
				"System.Collections.Generic.IEnumerable`1" => ReturnKind.SyncEnumerable,
				_ => ReturnKind.Value,
			};
		}

		internal static bool IsUnsupported(string returnText) {
			return IsUnsupported(SyntaxFactory.ParseTypeName(returnText), true);
		}

		internal static bool IsUnsupportedInterface(string typeText) {
			return IsUnsupportedInterface(TypeOf(typeText));
		}

		private static bool IsUnsupportedInterface(TypeSyntax type) {
			return NameOf(type) is "System.Collections.IEnumerable" or "System.Collections.IEnumerator"
				or "System.Collections.Generic.IEnumerator`1" or "System.Collections.Generic.IAsyncEnumerator`1"
				or "System.Linq.IQueryable" or "System.Linq.IQueryable`1" or "System.Linq.IOrderedQueryable`1"
				or "System.Linq.IOrderedEnumerable`1" or "System.IObservable`1";
		}

		private static bool IsUnsupported(TypeSyntax type, bool root) {
			if (type is NullableTypeSyntax nullable) {
				if (NameOf(nullable.ElementType) is "System.Threading.Tasks.ValueTask" or "System.Threading.Tasks.ValueTask`1") return true;
				return IsUnsupported(nullable.ElementType, root);
			}
			if (!root && Of(type) != ReturnKind.Value) return true;
			if (IsUnsupportedInterface(type)) return true;
			return type switch {
				ArrayTypeSyntax array => IsUnsupported(array.ElementType, false),
				GenericNameSyntax generic => generic.TypeArgumentList.Arguments.Any(argument => IsUnsupported(argument, false)),
				QualifiedNameSyntax qualified => IsUnsupported(qualified.Left, false) || IsUnsupported(qualified.Right, false),
				AliasQualifiedNameSyntax alias => IsUnsupported(alias.Name, root),
				_ => false,
			};
		}

		internal static TypeSyntax TypeOf(string text) {
			var type = SyntaxFactory.ParseTypeName(text);
			return type is NullableTypeSyntax nullable ? nullable.ElementType : type;
		}

		private static string NameOf(TypeSyntax type) {
			return type switch {
				IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
				GenericNameSyntax generic => generic.Identifier.ValueText + "`" + generic.TypeArgumentList.Arguments.Count,
				QualifiedNameSyntax qualified => NameOf(qualified.Left) + "." + NameOf(qualified.Right),
				AliasQualifiedNameSyntax alias when alias.Alias.Identifier.ValueText == "global" => NameOf(alias.Name),
				_ => "",
			};
		}
	}

	private static string DelegateType(ExportInfo e) {
		var isVoid = e.ReturnText == BridgeTypeText.VoidText;
		var parameterTypes = string.Join(", ", e.Parameters.Select(p => p.TypeCSharp));
		if (e.Parameters.Length == 0) {
			return isVoid ? "global::System.Action" : $"global::System.Func<{e.ReturnCSharp}>";
		}
		return isVoid ? $"global::System.Action<{parameterTypes}>" : $"global::System.Func<{parameterTypes}, {e.ReturnCSharp}>";
	}

	private static string ManifestLine(ExportInfo e) {
		var kind = e.IsInstance ? "instance" : "static";
		var parameters = string.Join(";", e.Parameters.Select(p => $"{(p.IsParams ? "p" : "-")}:{p.Name}:{p.TypeText}"));
		return $"{e.Name}|{kind}|{e.ReturnText}|{parameters}";
	}

	private static string EscapeLiteral(string value) {
		return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
	}

}
