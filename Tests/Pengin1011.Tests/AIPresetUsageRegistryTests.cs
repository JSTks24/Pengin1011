using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

public sealed class AIPresetUsageRegistryTests : IDisposable {
	private static readonly Lazy<Assembly> _goodProvider = new(() => Compile("""
		using Pengin1011.Services.AI;
		[assembly: AIPresetUsageProvider(typeof(GoodProvider))]
		internal sealed class GoodProvider : IAIPresetUsageProvider {
			public void Register(AIPresetUsageRegistrar registrar) {
				registrar.AddNamed("Looks.Module.Type#Method", "prod");
				registrar.AddDefault("Looks.Module.Type#Default");
			}
		}
		""", "AiUsageGood"));

	private static readonly Lazy<Assembly> _throwingProvider = new(() => Compile("""
		using System;
		using Pengin1011.Services.AI;
		[assembly: AIPresetUsageProvider(typeof(ThrowingProvider))]
		internal sealed class ThrowingProvider : IAIPresetUsageProvider {
			public void Register(AIPresetUsageRegistrar registrar) {
				registrar.AddNamed("Looks.Module.Type#Partial", "prod");
				throw new InvalidOperationException("清单损坏");
			}
		}
		""", "AiUsageBad"));

	private static Assembly Compile(string source, string assemblyName) {
		var references = GeneratorTestHost.ReferenceSet().ToArray();
		var compilation = CSharpCompilation.Create(
			assemblyName,
			[CSharpSyntaxTree.ParseText(source)],
			references,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
		using var stream = new MemoryStream();
		var result = compilation.Emit(stream);
		Assert.True(result.Success, string.Join(" | ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
		return Assembly.Load(stream.ToArray());
	}

	public AIPresetUsageRegistryTests() {
		AIPresetUsageRegistry.ForgetAll();
	}

	public void Dispose() {
		AIPresetUsageRegistry.ForgetAll();
	}

	[Fact]
	public void Register_WithoutAttribute_IsSilent() {
		AIPresetUsageRegistry.Register(typeof(AIPresetUsageRegistryTests).Assembly, "TestAssembly");

		Assert.Empty(AIPresetUsageRegistry.Usages);
	}

	[Fact]
	public void Register_WithProvider_AddsEveryEntry() {
		AIPresetUsageRegistry.Register(_goodProvider.Value, "GoodModule");

		var usages = AIPresetUsageRegistry.Usages;
		Assert.Equal(2, usages.Count);
		Assert.All(usages, usage => Assert.Equal("GoodModule", usage.Owner));
		Assert.Contains(usages, usage => usage.CallSite == "Looks.Module.Type#Method" && usage.Dependency.Name == "prod");
		Assert.Contains(usages, usage => usage.CallSite == "Looks.Module.Type#Default" && usage.Dependency.IsDefault);
	}

	[Fact]
	public void Register_RepeatedForSameOwner_DoesNotDuplicate() {
		AIPresetUsageRegistry.Register(_goodProvider.Value, "GoodModule");
		AIPresetUsageRegistry.Register(_goodProvider.Value, "GoodModule");

		Assert.Equal(2, AIPresetUsageRegistry.Usages.Count);
	}

	[Fact]
	public void Register_ThrowingProvider_RevokesAndRethrows() {
		Assert.Throws<InvalidOperationException>(() => AIPresetUsageRegistry.Register(_throwingProvider.Value, "BadModule"));

		Assert.Empty(AIPresetUsageRegistry.Usages);
		Assert.Empty(AIPresetUsageRegistry.ReferencesTo("prod"));
	}

	[Fact]
	public void ReferencesTo_IgnoresDefaultDependency() {
		AIPresetUsageRegistry.Register(_goodProvider.Value, "GoodModule");

		Assert.Equal(["GoodModule: Looks.Module.Type#Method"], AIPresetUsageRegistry.ReferencesTo("prod"));
		Assert.Empty(AIPresetUsageRegistry.ReferencesTo("<default>"));
	}

	[Fact]
	public void ReferencesTo_IsOrdinal() {
		AIPresetUsageRegistry.Register(_goodProvider.Value, "GoodModule");

		Assert.Empty(AIPresetUsageRegistry.ReferencesTo("Prod"));
	}

	[Fact]
	public void ForgetAll_ClearsEntries() {
		AIPresetUsageRegistry.Register(_goodProvider.Value, "GoodModule");
		Assert.NotEmpty(AIPresetUsageRegistry.Usages);

		AIPresetUsageRegistry.ForgetAll();

		Assert.Empty(AIPresetUsageRegistry.Usages);
		Assert.Empty(AIPresetUsageRegistry.ReferencesTo("prod"));
	}

	[Fact]
	public void Rollback_RemovesOnlyTheFailedBatch() {
		AIPresetUsageRegistry.Register(_goodProvider.Value, "GoodModule");

		Assert.Throws<InvalidOperationException>(() => AIPresetUsageRegistry.BeginRegistration(_throwingProvider.Value, "BadModule"));

		Assert.Equal(2, AIPresetUsageRegistry.Usages.Count);
		Assert.Equal(["GoodModule: Looks.Module.Type#Method"], AIPresetUsageRegistry.ReferencesTo("prod"));
	}

	[Fact]
	public void BeginRegistration_WithoutCommit_LeavesNothingBehind() {
		var batch = AIPresetUsageRegistry.BeginRegistration(_goodProvider.Value, "DeferredModule");
		Assert.NotNull(batch);

		Assert.Empty(AIPresetUsageRegistry.Usages);

		AIPresetUsageRegistry.Commit(batch);
		Assert.Equal(2, AIPresetUsageRegistry.Usages.Count);
	}
}
