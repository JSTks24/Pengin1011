using System.Resources;
using Microsoft.CodeAnalysis;

namespace Pengin1011Generator.ModuleBridge;

internal static class BridgeDiagnostics {
	internal static readonly ResourceManager Manager = new("Pengin1011Generator.ModuleBridge.Resources.DiagnosticStrings", typeof(BridgeDiagnostics).Assembly);

	internal const string Category = "ModuleBridge";

	internal static readonly DiagnosticDescriptor RuleMethodNotPublic = Rule("PBG001", "MethodNotPublic");
	internal static readonly DiagnosticDescriptor RuleTypeNotPublic = Rule("PBG002", "TypeNotPublic");
	internal static readonly DiagnosticDescriptor RuleInstanceNotOnRuntime = Rule("PBG003", "InstanceNotOnRuntime");
	internal static readonly DiagnosticDescriptor RuleGenericDeclaringType = Rule("PBG004", "GenericDeclaringType");
	internal static readonly DiagnosticDescriptor RuleGenericMethod = Rule("PBG005", "GenericMethod");
	internal static readonly DiagnosticDescriptor RuleByRefSignature = Rule("PBG006", "ByRefSignature");
	internal static readonly DiagnosticDescriptor RuleOptionalParameter = Rule("PBG007", "OptionalParameter");
	internal static readonly DiagnosticDescriptor RuleLocalType = Rule("PBG008", "LocalTypeSignature");
	internal static readonly DiagnosticDescriptor RuleTooManyParameters = Rule("PBG009", "TooManyParameters");
	internal static readonly DiagnosticDescriptor RuleUnsupportedType = Rule("PBG010", "UnsupportedType");
	internal static readonly DiagnosticDescriptor RuleDuplicateExport = Rule("PBG011", "DuplicateExport");
	internal static readonly DiagnosticDescriptor RuleInvalidModuleName = Rule("PBG012", "InvalidModuleName");
	internal static readonly DiagnosticDescriptor RuleNameCollision = Rule("PBG013", "NameCollision");
	internal static readonly DiagnosticDescriptor RuleManifestParse = Rule("PBG101", "ManifestParse");
	internal static readonly DiagnosticDescriptor RuleManifestModuleInvalid = Rule("PBG102", "ManifestModuleInvalid");
	internal static readonly DiagnosticDescriptor RuleManifestFormatOutdated = Rule("PBG103", "ManifestFormatOutdated");
	internal static readonly DiagnosticDescriptor RuleManifestCountMismatch = Rule("PBG104", "ManifestCountMismatch");
	internal static readonly DiagnosticDescriptor RuleManifestDuplicateModule = Rule("PBG105", "ManifestDuplicateModule");
	internal static readonly DiagnosticDescriptor RuleManifestDuplicateExport = Rule("PBG106", "ManifestDuplicateExport");

	internal static DiagnosticDescriptor Rule(string id, string messageKey) {
		return new DiagnosticDescriptor(
			id,
			new LocalizableResourceString(messageKey, Manager, typeof(BridgeDiagnostics)),
			new LocalizableResourceString(messageKey, Manager, typeof(BridgeDiagnostics)),
			Category,
			DiagnosticSeverity.Error,
			true);
	}
}
