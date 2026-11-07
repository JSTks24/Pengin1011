using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Resources;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Pengin1011Generator.Database;

[Generator]
public sealed class DatabaseGenerator : IIncrementalGenerator {
	private static readonly ResourceManager _resources = new("Pengin1011Generator.Database.Resources.DiagnosticStrings", typeof(DatabaseGenerator).Assembly);
	private static readonly DiagnosticDescriptor _shape = Rule("PDB001", "Declaration");
	private static readonly DiagnosticDescriptor _identifier = Rule("PDB002", "Identifier");
	private static readonly DiagnosticDescriptor _duplicate = Rule("PDB003", "Duplicate");
	private static readonly DiagnosticDescriptor _collision = Rule("PDB004", "Collision");

	public void Initialize(IncrementalGeneratorInitializationContext context) {
		var declarations = context.SyntaxProvider.ForAttributeWithMetadataName(
			"Pengin1011.Core.ModuleDatabaseAttribute",
			static (node, _) => node is ClassDeclarationSyntax,
			static (ctx, _) => (Type: (INamedTypeSymbol)ctx.TargetSymbol, Id: ctx.Attributes[0].ConstructorArguments.FirstOrDefault().Value as string));
		context.RegisterSourceOutput(declarations.Collect().Combine(context.CompilationProvider),
			static (spc, pair) => Emit(spc, pair.Left, pair.Right));
	}

	private static DiagnosticDescriptor Rule(string id, string key) {
		var message = new LocalizableResourceString(key, _resources, typeof(DatabaseGenerator));
		return new DiagnosticDescriptor(id, message, message, "Database", DiagnosticSeverity.Error, true);
	}

	private static void Emit(SourceProductionContext context, ImmutableArray<(INamedTypeSymbol Type, string? Id)> declarations, Compilation compilation) {
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var valid = new List<(INamedTypeSymbol Type, string Id)>();
		foreach (var item in declarations.OrderBy(item => item.Type.ToDisplayString(), StringComparer.Ordinal)) {
			var type = item.Type;
			var location = type.Locations.FirstOrDefault();
			if (type.DeclaredAccessibility != Accessibility.Public || type.IsAbstract || type.IsStatic || type.Arity != 0 || type.ContainingType != null
				|| type.BaseType?.ToDisplayString() != "Microsoft.EntityFrameworkCore.DbContext"
				|| type.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is not ClassDeclarationSyntax syntax || !syntax.Modifiers.Any(SyntaxKind.PartialKeyword))) {
				context.ReportDiagnostic(Diagnostic.Create(_shape, location, type.Name));
				continue;
			}
			if (!IsIdentifier(item.Id)) {
				context.ReportDiagnostic(Diagnostic.Create(_identifier, location, item.Id ?? ""));
				continue;
			}
			if (!seen.Add(item.Id!)) {
				context.ReportDiagnostic(Diagnostic.Create(_duplicate, location, item.Id));
				continue;
			}
			var ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".";
			if (type.InstanceConstructors.Any(ctor => !ctor.IsImplicitlyDeclared) || type.GetMembers("OnConfiguring").Length != 0
				|| compilation.GetTypeByMetadataName(ns + type.Name + "DesignFactory") != null
				|| compilation.GetTypeByMetadataName("Pengin1011.Generated.__ModuleDatabaseProvider") != null) {
				context.ReportDiagnostic(Diagnostic.Create(_collision, location, type.Name));
				continue;
			}
			valid.Add((type, item.Id!));
			var name = "@" + type.Name;
			var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			var builder = new StringBuilder("#nullable enable\n");
			if (ns.Length > 0) builder.Append("namespace ").Append(ns.TrimEnd('.')).Append(";\n\n");
			builder.Append("public partial class ").Append(name).Append(" {\n\tpublic ").Append(name)
				.Append("(global::Microsoft.EntityFrameworkCore.DbContextOptions<").Append(fullName).Append("> options) : base(options) {\n\t}\n}\n\n");
			builder.Append("public sealed class @").Append(type.Name).Append("DesignFactory : global::Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<")
				.Append(fullName).Append("> {\n\tpublic ").Append(fullName).Append(" CreateDbContext(string[] args) {\n")
				.Append("\t\tvar options = global::Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(new global::Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<")
				.Append(fullName).Append(">(), ").Append(SymbolDisplay.FormatLiteral("Data Source=" + item.Id + "-design.db", true)).Append(").Options;\n")
				.Append("\t\treturn new ").Append(fullName).Append("(options);\n\t}\n}\n");
			context.AddSource(type.ToDisplayString() + ".Database.g.cs", SourceText.From(builder.ToString(), new UTF8Encoding(false)));
		}
		if (valid.Count == 0) return;
		var provider = new StringBuilder("#nullable enable\n[assembly: global::Pengin1011.Core.ModuleDatabaseProvider(typeof(global::Pengin1011.Generated.__ModuleDatabaseProvider))]\nnamespace Pengin1011.Generated;\n\ninternal sealed class __ModuleDatabaseProvider : global::Pengin1011.Core.IModuleDatabaseProvider {\n\tpublic void Register(global::Pengin1011.Core.DatabaseRegistrar registrar) {\n");
		foreach (var item in valid) {
			var type = item.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			provider.Append("\t\tregistrar.Add<").Append(type).Append(">(").Append(SymbolDisplay.FormatLiteral(item.Id, true))
				.Append(", static options => new ").Append(type).Append("(options));\n");
		}
		provider.Append("\t}\n}\n");
		context.AddSource("ModuleDatabases.g.cs", SourceText.From(provider.ToString(), new UTF8Encoding(false)));
	}

	private static bool IsIdentifier(string? id) {
		if (string.IsNullOrWhiteSpace(id) || id!.Contains("..") || id.Any(character => char.IsControl(character) || "<>:\"/\\|?*;= .".Contains(character))) return false;
		var upper = id.ToUpperInvariant();
		return upper != "CON" && upper != "PRN" && upper != "AUX" && upper != "NUL"
			&& !(upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal)) && upper[3] >= '1' && upper[3] <= '9');
	}
}
