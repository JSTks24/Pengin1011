using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ModuleHostContractTests {
	[Fact]
	public void GeneratedContract_MatchesTheCurrentHostVersion() {
		var outcome = GeneratorTestHost.Run("public static class Exports { }", "CurrentContract_" + Guid.NewGuid().ToString("N"));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		using var stream = new MemoryStream();
		var result = outcome.OutputCompilation.Emit(stream);
		Assert.True(result.Success, string.Join(" | ", result.Diagnostics));
		var assembly = System.Reflection.Assembly.Load(stream.ToArray());
		Assert.True(ModuleHostContract.TryRead(assembly, out var version, out var failure), failure);
		Assert.Equal(ModuleHostContract.Current, version);
	}

	[Fact]
	public async Task RenamedModule_IsRejectedBeforeRuntimeConstructionOrBridgeRegistration() {
		var directory = Path.Combine(Path.GetTempPath(), "pengin_renamed_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try {
			var marker = Path.Combine(directory, "runtime-created.txt");
			var providerMarker = Path.Combine(directory, "provider-created.txt");
			var assemblyName = "OriginalModule_" + Guid.NewGuid().ToString("N");
			var source = $$"""
using System.Threading;
using System.Threading.Tasks;
using Pengin1011.Core;
using Pengin1011.Core.Modules;
[assembly: ModuleHostContract({{ModuleHostContract.Current}})]
[assembly: ModuleBridgeProvider(typeof(RenamedProvider))]
public sealed class RenamedRuntime : IModuleRuntime {
	public RenamedRuntime() {
		System.IO.File.WriteAllText({{SymbolDisplay.FormatLiteral(marker, true)}}, "created");
	}
	public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
	public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
public sealed class RenamedProvider : IModuleBridgeProvider {
	public RenamedProvider() {
		System.IO.File.WriteAllText({{SymbolDisplay.FormatLiteral(providerMarker, true)}}, "created");
	}
	public void Register(ModuleBridgeRegistrar registrar, IModuleRuntime runtime) {
		registrar.Add("RenamedValue", (System.Func<string>)(() => "renamed"));
	}
}
""";
			var compilation = CSharpCompilation.Create(
				assemblyName,
				[CSharpSyntaxTree.ParseText(source)],
				GeneratorTestHost.ReferenceSet(),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
			var path = Path.Combine(directory, "RenamedModule.dll");
			using (var output = File.Create(path)) {
				var result = compilation.Emit(output);
				Assert.True(result.Success, string.Join(" | ", result.Diagnostics));
			}

			await TestChildProcess.RunArgumentsAsync("module-name-reject", path, assemblyName);

			Assert.False(File.Exists(marker));
			Assert.False(File.Exists(providerMarker));
		} finally {
			Directory.Delete(directory, true);
		}
	}

	[Fact]
	public async Task LegacyContract_IsRejectedBeforeRuntimeConstructionOrBridgeRegistration() {
		var directory = Path.Combine(Path.GetTempPath(), "pengin_contract_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try {
			var marker = Path.Combine(directory, "runtime-created.txt");
			var source = $$"""
using System.Threading;
using System.Threading.Tasks;
using Pengin1011.Core;
using Pengin1011.Core.Modules;
[assembly: ModuleHostContract(3)]
[assembly: ModuleBridgeProvider(typeof(LegacyProvider))]
public sealed class LegacyRuntime : IModuleRuntime {
	public LegacyRuntime() {
		System.IO.File.WriteAllText({{SymbolDisplay.FormatLiteral(marker, true)}}, "created");
	}
	public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
	public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
public sealed class LegacyProvider : IModuleBridgeProvider {
	public void Register(ModuleBridgeRegistrar registrar, IModuleRuntime runtime) {
		registrar.Add("LegacyValue", (System.Func<string>)(() => "legacy"));
	}
}
""";
			var assemblyName = "LegacyContract_" + Guid.NewGuid().ToString("N");
			var compilation = CSharpCompilation.Create(
				assemblyName,
				[CSharpSyntaxTree.ParseText(source)],
				GeneratorTestHost.ReferenceSet(),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
			var path = Path.Combine(directory, assemblyName + ".dll");
			using (var output = File.Create(path)) {
				var result = compilation.Emit(output);
				Assert.True(result.Success, string.Join(" | ", result.Diagnostics));
			}

			await TestChildProcess.RunArgumentsAsync("module-contract-reject", path);

			Assert.False(File.Exists(marker));
		} finally {
			Directory.Delete(directory, true);
		}
	}
}
