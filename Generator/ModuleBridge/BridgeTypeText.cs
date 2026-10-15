using System;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Pengin1011Generator.ModuleBridge;

internal sealed class BridgeTypeScope {
	private string? _frameworkDirectory;
	private bool _frameworkResolved;

	internal BridgeTypeScope(Compilation compilation, string moduleAssemblyName) {
		Compilation = compilation;
		ModuleAssemblyName = moduleAssemblyName;
	}

	internal Compilation Compilation { get; }

	internal string ModuleAssemblyName { get; }

	internal string? FrameworkDirectory {
		get {
			if (_frameworkResolved) return _frameworkDirectory;
			_frameworkResolved = true;
			var core = Compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly;
			if (core != null && Compilation.GetMetadataReference(core) is PortableExecutableReference { FilePath: { Length: > 0 } path }) {
				_frameworkDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
			}
			return _frameworkDirectory;
		}
	}
}

internal static class BridgeTypeText {
	internal const string HostAssemblyName = "Pengin1011";
	internal const string VoidText = "void";

	internal static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
		.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

	internal enum SerializeFailure {
		None,
		Unsupported,
		LocalType,
	}

	internal static string? TrySerialize(ITypeSymbol type, BridgeTypeScope scope, out SerializeFailure failure) {
		failure = SerializeFailure.None;
		if (!Validate(type, scope, ref failure)) return null;
		var text = type.ToDisplayString(TypeFormat);
		if (!IsWellFormed(text)) {
			failure = SerializeFailure.Unsupported;
			return null;
		}
		return text;
	}

	internal static string? TryToCSharp(string text) {
		if (text.Length == 0) return null;
		if (string.Equals(text, VoidText, StringComparison.Ordinal)) return text;
		var parsed = SyntaxFactory.ParseTypeName(text, 0, true);
		if (parsed.ContainsDiagnostics || parsed.FullSpan.Length != text.Length) return null;
		return text;
	}

	internal static string QualifiedName(INamedTypeSymbol named) {
		return named.ToDisplayString(TypeFormat);
	}

	internal static string EscapeIdentifier(string name) {
		if (SyntaxFacts.IsValidIdentifier(name) && !SyntaxFacts.IsReservedKeyword(SyntaxFacts.GetKeywordKind(name))) return name;
		return "@" + name;
	}

	internal static bool IsValidModuleName(string name) {
		return name.Length > 0 && SyntaxFacts.IsValidIdentifier(name);
	}

	private static bool IsWellFormed(string text) {
		var parsed = SyntaxFactory.ParseTypeName(text, 0, true);
		return !parsed.ContainsDiagnostics && parsed.FullSpan.Length == text.Length;
	}

	private static bool Validate(ITypeSymbol type, BridgeTypeScope scope, ref SerializeFailure failure) {
		switch (type) {
			case IArrayTypeSymbol array:
				if (array.Rank != 1) {
					failure = SerializeFailure.Unsupported;
					return false;
				}
				return Validate(array.ElementType, scope, ref failure);
			case IPointerTypeSymbol:
			case IFunctionPointerTypeSymbol:
			case ITypeParameterSymbol:
				failure = SerializeFailure.Unsupported;
				return false;
			case INamedTypeSymbol named:
				return ValidateNamed(named, scope, ref failure);
			default:
				failure = SerializeFailure.Unsupported;
				return false;
		}
	}

	private static bool ValidateNamed(INamedTypeSymbol named, BridgeTypeScope scope, ref SerializeFailure failure) {
		if (named.IsTupleType || named.IsUnboundGenericType || named.TypeKind == TypeKind.Error || named.SpecialType == SpecialType.System_Void) {
			failure = SerializeFailure.Unsupported;
			return false;
		}
		for (var current = named; current != null; current = current.ContainingType) {
			if (current.DeclaredAccessibility != Accessibility.Public) {
				failure = SerializeFailure.LocalType;
				return false;
			}
			if (!IsAllowedAssembly(current, scope, ref failure)) return false;
			foreach (var argument in current.TypeArguments) {
				if (!Validate(argument, scope, ref failure)) return false;
			}
		}
		return true;
	}

	private static bool IsAllowedAssembly(INamedTypeSymbol type, BridgeTypeScope scope, ref SerializeFailure failure) {
		var assembly = type.ContainingAssembly;
		if (assembly == null) {
			failure = SerializeFailure.Unsupported;
			return false;
		}
		if (string.Equals(assembly.Name, HostAssemblyName, StringComparison.Ordinal)) return true;
		if (string.Equals(assembly.Name, scope.ModuleAssemblyName, StringComparison.Ordinal)) {
			failure = SerializeFailure.LocalType;
			return false;
		}
		if (IsFrameworkReference(assembly, scope)) return true;
		failure = SerializeFailure.Unsupported;
		return false;
	}

	private static bool IsFrameworkReference(IAssemblySymbol assembly, BridgeTypeScope scope) {
		if (scope.Compilation.GetMetadataReference(assembly) is not PortableExecutableReference reference) return false;
		var path = reference.FilePath;
		if (string.IsNullOrEmpty(path)) return false;
		var framework = scope.FrameworkDirectory;
		if (string.IsNullOrEmpty(framework)) return false;
		var directory = Path.GetDirectoryName(Path.GetFullPath(path));
		return directory != null && string.Equals(directory, framework, StringComparison.OrdinalIgnoreCase);
	}
}
