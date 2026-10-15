using System.Resources;
using Microsoft.CodeAnalysis;

namespace Pengin1011Generator.AIPreset;

internal static class AIPresetDiagnostics {
	internal static readonly ResourceManager Manager = new("Pengin1011Generator.AIPreset.Resources.DiagnosticStrings", typeof(AIPresetDiagnostics).Assembly);

	internal const string Category = "AIPreset";

	internal static readonly DiagnosticDescriptor RulePresetNameNotConstant = Rule("PAP001", "PresetNameNotConstant");

	internal static readonly DiagnosticDescriptor RulePresetNameBypassed = Rule("PAP002", "PresetNameBypassed");

	internal static DiagnosticDescriptor Rule(string id, string messageKey) {
		return new DiagnosticDescriptor(
			id,
			new LocalizableResourceString(messageKey, Manager, typeof(AIPresetDiagnostics)),
			new LocalizableResourceString(messageKey, Manager, typeof(AIPresetDiagnostics)),
			Category,
			DiagnosticSeverity.Error,
			true);
	}
}
